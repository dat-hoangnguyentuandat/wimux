using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Wimux.Core.Services;

/// <summary>
/// Downloads, parses, and matches uBlock-compatible filter lists for in-app WebView2 ad blocking.
/// Covers network request blocking + cosmetic CSS hiding.
/// </summary>
public class AdBlockService
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "wimux", "adblock");

    // Default filter list subscriptions matching uBlock Origin defaults
    private static readonly (string Name, string Url)[] DefaultLists =
    [
        ("ublock-filters",   "https://ublockorigin.github.io/uAssets/filters/filters.txt"),
        ("ublock-badware",   "https://ublockorigin.github.io/uAssets/filters/badware.txt"),
        ("ublock-privacy",   "https://ublockorigin.github.io/uAssets/filters/privacy.txt"),
        ("ublock-unbreak",   "https://ublockorigin.github.io/uAssets/filters/unbreak.txt"),
        ("ublock-quickfix",  "https://ublockorigin.github.io/uAssets/filters/quick-fixes.txt"),
        ("easylist",         "https://ublockorigin.github.io/uAssets/thirdparties/easylist.txt"),
        ("easyprivacy",      "https://ublockorigin.github.io/uAssets/thirdparties/easyprivacy.txt"),
        ("peter-lowe",       "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=hosts&showintro=1&mimetype=plaintext"),
    ];

    // Network blocking structures
    private readonly HashSet<string> _blockedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Regex> _blockedPatterns = [];
    private readonly HashSet<string> _allowedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Regex> _allowedPatterns = [];

    // Cosmetic filters: generic selectors + per-host selectors
    private readonly HashSet<string> _genericCssSelectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _hostCssSelectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _hostCssExceptions = new(StringComparer.OrdinalIgnoreCase);

    // Scriptlet filters: host -> list of (name, args). Parsed from host##+js(name, arg1, …)
    // rules (uBlock "+js" injections). Only a curated set of scriptlet names is
    // executed at runtime (see GetScriptletRuntime); unknown names are stored but
    // become no-ops, matching uBlock's behaviour for unsupported scriptlets.
    private readonly Dictionary<string, List<ScriptletRule>> _hostScriptlets = new(StringComparer.OrdinalIgnoreCase);

    private sealed record ScriptletRule(string Name, IReadOnlyList<string> Args);

    private bool _loaded;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public bool IsEnabled { get; set; } = true;

    // Returns the cosmetic injection bootstrap JS to be called via AddScriptToExecuteOnDocumentCreatedAsync
    public string GetCosmeticBootstrapScript()
    {
        return """
(function() {
  function wimuxInjectCosmetic(selectors) {
    if (!selectors || selectors.length === 0) return;
    var style = document.createElement('style');
    style.id = '__wimux_adblock__';
    style.textContent = selectors.map(function(s){ return s + '{display:none!important;visibility:hidden!important;}'; }).join('\n');
    (document.head || document.documentElement).appendChild(style);
  }

  function wimuxApplyCosmetic() {
    var host = location.hostname;
    window.chrome && chrome.webview && chrome.webview.postMessage(JSON.stringify({type:'wimux-cosmetic-req', host: host}));
  }

  window.chrome && chrome.webview && chrome.webview.addEventListener('message', function(e) {
    try {
      var msg = JSON.parse(e.data);
      if (msg.type === 'wimux-cosmetic-res') {
        wimuxInjectCosmetic(msg.selectors);
      }
    } catch(_) {}
  });

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', wimuxApplyCosmetic);
  } else {
    wimuxApplyCosmetic();
  }
})();
""";
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await _loadLock.WaitAsync();
        try
        {
            if (_loaded) return;
            await LoadFromCacheAsync();
            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task UpdateFiltersAsync(IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(CacheDir);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        foreach (var (name, url) in DefaultLists)
        {
            try
            {
                progress?.Report($"Downloading {name}…");
                var text = await http.GetStringAsync(url);
                var path = Path.Combine(CacheDir, name + ".txt");
                await File.WriteAllTextAsync(path, text);
            }
            catch (Exception ex)
            {
                progress?.Report($"Failed {name}: {ex.Message}");
            }
        }

        progress?.Report("Parsing filters…");
        await _loadLock.WaitAsync();
        try
        {
            ClearRules();
            await LoadFromCacheAsync();
        }
        finally
        {
            _loadLock.Release();
        }

        progress?.Report("Done.");
    }

    private async Task LoadFromCacheAsync()
    {
        ClearRules();

        if (!Directory.Exists(CacheDir))
            return;

        foreach (var (name, _) in DefaultLists)
        {
            var path = Path.Combine(CacheDir, name + ".txt");
            if (!File.Exists(path)) continue;

            try
            {
                var lines = await File.ReadAllLinesAsync(path);
                ParseFilterList(lines);
            }
            catch { /* skip corrupted cache file */ }
        }
    }

    private void ClearRules()
    {
        _blockedHosts.Clear();
        _blockedPatterns.Clear();
        _allowedHosts.Clear();
        _allowedPatterns.Clear();
        _genericCssSelectors.Clear();
        _hostCssSelectors.Clear();
        _hostCssExceptions.Clear();
        _hostScriptlets.Clear();
    }

    private void ParseFilterList(string[] lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('!') || line.StartsWith('['))
                continue;

            // Hosts-file format: "0.0.0.0 ads.example.com" or "127.0.0.1 ads.example.com"
            if (line.StartsWith("0.0.0.0 ") || line.StartsWith("127.0.0.1 "))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    _blockedHosts.Add(parts[1].ToLowerInvariant());
                continue;
            }

            // Cosmetic filter: ##selector or host##selector
            var cosmeticIdx = line.IndexOf("##", StringComparison.Ordinal);
            if (cosmeticIdx >= 0 && !line.StartsWith("@@"))
            {
                var hostPart = line[..cosmeticIdx];
                var selector = line[(cosmeticIdx + 2)..];

                // Scriptlet injection: host##+js(name, arg1, arg2, …)
                if (selector.StartsWith("+js(", StringComparison.Ordinal) && selector.EndsWith(")", StringComparison.Ordinal))
                {
                    var inner = selector[4..^1];
                    ParseScriptletRule(hostPart, inner);
                    continue;
                }

                // Skip remaining scriptlet/HTML filters: ##^ (HTML filtering)
                if (selector.StartsWith('+') || selector.StartsWith('^'))
                    continue;

                if (string.IsNullOrEmpty(hostPart))
                {
                    _genericCssSelectors.Add(selector);
                }
                else
                {
                    foreach (var h in hostPart.Split(','))
                    {
                        var cssHost = h.Trim().TrimStart('~');
                        if (string.IsNullOrEmpty(cssHost)) continue;
                        if (!_hostCssSelectors.TryGetValue(cssHost, out var set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            _hostCssSelectors[cssHost] = set;
                        }
                        set.Add(selector);
                    }
                }
                continue;
            }

            // Cosmetic exception: #@#selector
            var cosmeticExIdx = line.IndexOf("#@#", StringComparison.Ordinal);
            if (cosmeticExIdx >= 0)
            {
                var hostPart = line[..cosmeticExIdx];
                var selector = line[(cosmeticExIdx + 3)..];
                foreach (var h in hostPart.Split(','))
                {
                    var exHost = h.Trim();
                    if (string.IsNullOrEmpty(exHost)) continue;
                    if (!_hostCssExceptions.TryGetValue(exHost, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        _hostCssExceptions[exHost] = set;
                    }
                    set.Add(selector);
                }
                continue;
            }

            // Network exception: @@...
            if (line.StartsWith("@@"))
            {
                var exRule = line[2..];
                var exHost = ExtractHostFromRule(exRule);
                if (exHost != null)
                    _allowedHosts.Add(exHost);
                else
                {
                    var rx = TryBuildRegex(exRule);
                    if (rx != null) _allowedPatterns.Add(rx);
                }
                continue;
            }

            // Network block rule
            var host = ExtractHostFromRule(line);
            if (host != null)
            {
                _blockedHosts.Add(host);
            }
            else
            {
                var rx = TryBuildRegex(line);
                if (rx != null) _blockedPatterns.Add(rx);
            }
        }
    }

    /// <summary>
    /// Parse a "+js(...)" scriptlet rule body into per-host ScriptletRule entries.
    /// hostPart is the comma-separated host list before "##"; inner is the
    /// content between "+js(" and the closing ")".
    /// </summary>
    private void ParseScriptletRule(string hostPart, string inner)
    {
        if (string.IsNullOrWhiteSpace(hostPart) || string.IsNullOrWhiteSpace(inner))
            return;

        var tokens = SplitScriptletArgs(inner);
        if (tokens.Count == 0) return;

        var name = tokens[0].Trim();
        if (name.Length == 0) return;

        // Only keep scriptlets our runtime can actually execute. Unsupported
        // ones (DOM-bypass, rpnt, trusted-* request editors, …) are skipped so
        // we don't carry dead weight — the curated set below covers YouTube
        // ad removal via the json-prune / replace-response family.
        if (!SupportedScriptlets.Contains(NormalizeScriptletName(name)))
            return;

        var args = tokens.Skip(1).Select(StripScriptletQuotes).ToList();
        var rule = new ScriptletRule(NormalizeScriptletName(name), args);

        foreach (var h in hostPart.Split(','))
        {
            var host = h.Trim();
            // Negated hosts (~example.com) mean "everywhere except"; we only
            // support positive host scoping, so skip the negations.
            if (host.Length == 0 || host.StartsWith('~')) continue;
            if (!_hostScriptlets.TryGetValue(host, out var list))
            {
                list = [];
                _hostScriptlets[host] = list;
            }
            list.Add(rule);
        }
    }

    /// uBlock scriptlet aliases collapse to a canonical name our JS runtime knows.
    private static string NormalizeScriptletName(string name) => name.Trim().ToLowerInvariant() switch
    {
        "json-prune.js" or "json-prune" => "json-prune",
        "json-prune-fetch-response.js" or "json-prune-fetch-response" => "json-prune-fetch-response",
        "json-prune-xhr-response.js" or "json-prune-xhr-response" => "json-prune-xhr-response",
        "trusted-replace-fetch-response.js" or "trusted-replace-fetch-response" => "trusted-replace-fetch-response",
        "trusted-replace-xhr-response.js" or "trusted-replace-xhr-response" => "trusted-replace-xhr-response",
        "set-constant.js" or "set-constant" or "set" => "set-constant",
        "no-setInterval-if.js" or "no-setInterval-if" or "nosiif" => "no-setInterval-if",
        "no-setTimeout-if.js" or "no-setTimeout-if" or "nostif" => "no-setTimeout-if",
        var n => n,
    };

    private static readonly HashSet<string> SupportedScriptlets = new(StringComparer.OrdinalIgnoreCase)
    {
        "json-prune",
        "json-prune-fetch-response",
        "json-prune-xhr-response",
        "trusted-replace-fetch-response",
        "trusted-replace-xhr-response",
        "set-constant",
        "no-setInterval-if",
        "no-setTimeout-if",
    };

    /// <summary>
    /// Split a scriptlet argument string on commas, honouring uBlock's "\,"
    /// escape (a literal comma inside an argument) and not splitting inside
    /// /regex/ literals or '…' / "…" quoted strings.
    /// </summary>
    private static List<string> SplitScriptletArgs(string inner)
    {
        var args = new List<string>();
        var sb = new System.Text.StringBuilder();
        char quote = '\0';
        var inRegex = false;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];

            if (c == '\\' && i + 1 < inner.Length && inner[i + 1] == ',')
            {
                // Escaped comma — emit a literal comma, consume both chars.
                sb.Append(',');
                i++;
                continue;
            }

            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }

            if (inRegex)
            {
                sb.Append(c);
                if (c == '/' && inner[i - 1] != '\\') inRegex = false;
                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    sb.Append(c);
                    break;
                case '/' when sb.ToString().Trim().Length == 0:
                    inRegex = true;
                    sb.Append(c);
                    break;
                case ',':
                    args.Add(sb.ToString().Trim());
                    sb.Clear();
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        if (sb.Length > 0 || args.Count > 0)
            args.Add(sb.ToString().Trim());
        return args;
    }

    private static string StripScriptletQuotes(string arg)
    {
        arg = arg.Trim();
        if (arg.Length >= 2 &&
            ((arg[0] == '\'' && arg[^1] == '\'') || (arg[0] == '"' && arg[^1] == '"')))
            return arg[1..^1];
        return arg;
    }

    private static string? ExtractHostFromRule(string rule)
    {
        // ||example.com^ — most common pattern, exact host block
        if (rule.StartsWith("||"))
        {
            var end = rule.IndexOfAny(['/', '^', '?', '*'], 2);
            var host = end < 0 ? rule[2..] : rule[2..end];
            if (host.Length > 0 && !host.Contains('*') && !host.Contains('~'))
                return host.ToLowerInvariant();
        }
        return null;
    }

    private static Regex? TryBuildRegex(string rule)
    {
        try
        {
            // Strip options after $
            var optIdx = rule.LastIndexOf('$');
            if (optIdx > 0) rule = rule[..optIdx];

            // Skip rules that are too vague or already covered
            if (rule.Length < 4 || rule == "*") return null;

            // Convert ABP wildcard/anchor syntax to regex
            var pattern = Regex.Escape(rule)
                .Replace(@"\|\|", @"(?:https?://(?:[^/]*\.)?)")
                .Replace(@"\|", "")
                .Replace(@"\*", ".*")
                .Replace(@"\^", @"(?:[/?#]|$)");

            return new Regex(pattern,
                RegexOptions.IgnoreCase | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(10));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns true if the given request URL should be blocked.
    /// </summary>
    public bool ShouldBlock(string url, string? documentHost = null)
    {
        if (!IsEnabled) return false;

        try
        {
            var uri = new Uri(url);
            var host = uri.Host.ToLowerInvariant();

            // Check allow list first
            if (_allowedHosts.Contains(host)) return false;
            if (documentHost != null && _allowedHosts.Contains(documentHost)) return false;

            // Host-exact block
            if (_blockedHosts.Contains(host)) return true;

            // Check host suffixes (e.g. rule "ads.example.com" matches "sub.ads.example.com")
            var dotHost = "." + host;
            foreach (var blocked in _blockedHosts)
            {
                if (dotHost.EndsWith("." + blocked, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Pattern-based block. Generic ABP regex patterns are noisy and
            // frequently match a site's own first-party asset/API URLs (this is
            // what was breaking TikTok — its own CDN/api calls tripped a generic
            // pattern). Skip regex matching for first-party requests, i.e. when
            // the request and document share a registrable domain. Host-list
            // blocks above still apply first-party (those are precise).
            var isFirstParty = documentHost != null &&
                string.Equals(RegistrableDomain(host), RegistrableDomain(documentHost), StringComparison.OrdinalIgnoreCase);
            if (!isFirstParty)
            {
                foreach (var rx in _blockedPatterns)
                {
                    if (rx.IsMatch(url)) return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort registrable domain (eTLD+1) for first-party comparison.
    /// Not PSL-accurate, but good enough to treat "v16.tiktokcdn.com" and
    /// "www.tiktok.com" as related: it returns the last two labels, and for
    /// common multi-part suffixes (co.uk, com.vn, …) the last three.
    /// </summary>
    private static string RegistrableDomain(string host)
    {
        var labels = host.Split('.');
        if (labels.Length <= 2) return host;

        var twoPartTlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "co.uk", "com.vn", "com.au", "co.jp", "com.br", "co.in", "com.cn", "com.tw", "co.kr",
        };
        var lastTwo = string.Join('.', labels[^2..]);
        return twoPartTlds.Contains(lastTwo)
            ? string.Join('.', labels[^3..])
            : lastTwo;
    }

    /// <summary>
    /// Returns CSS selectors to hide on the given hostname (generic + host-specific, minus exceptions).
    /// </summary>
    public IReadOnlyList<string> GetCosmeticSelectors(string hostname)
    {
        if (!IsEnabled) return [];

        var selectors = new HashSet<string>(_genericCssSelectors, StringComparer.OrdinalIgnoreCase);

        // Add host-specific selectors, walking up domain labels
        var labels = hostname.Split('.');
        for (var i = 0; i < labels.Length - 1; i++)
        {
            var suffix = string.Join('.', labels[i..]);
            if (_hostCssSelectors.TryGetValue(suffix, out var hostSet))
            {
                foreach (var s in hostSet) selectors.Add(s);
            }
        }

        // Remove exceptions
        if (_hostCssExceptions.TryGetValue(hostname, out var exceptions))
        {
            foreach (var ex in exceptions) selectors.Remove(ex);
        }

        // Limit to a reasonable count to avoid injecting huge payloads
        return selectors.Take(5000).ToList();
    }

    /// <summary>
    /// True if filter cache files are present on disk.
    /// </summary>
    public bool HasCachedFilters()
    {
        if (!Directory.Exists(CacheDir)) return false;
        return DefaultLists.Any(l => File.Exists(Path.Combine(CacheDir, l.Name + ".txt")));
    }

    // ── Scriptlet injection ────────────────────────────────────────────────

    /// <summary>
    /// Resolve the scriptlet rules that apply to a hostname, walking up domain
    /// labels so a rule on "youtube.com" also covers "www.youtube.com".
    /// </summary>
    private List<ScriptletRule> ResolveScriptlets(string hostname)
    {
        var rules = new List<ScriptletRule>();
        var labels = hostname.Split('.');
        for (var i = 0; i < labels.Length - 1; i++)
        {
            var suffix = string.Join('.', labels[i..]);
            if (_hostScriptlets.TryGetValue(suffix, out var list))
                rules.AddRange(list);
        }
        if (_hostScriptlets.TryGetValue(hostname, out var exact))
            rules.AddRange(exact);
        return rules;
    }

    /// <summary>
    /// Build the full JS payload to inject at document-creation for the given
    /// host: the scriptlet runtime plus one invocation per matched rule.
    /// Returns null when ad blocking is off or no scriptlets apply, so the
    /// caller can skip injection entirely.
    /// </summary>
    public string? BuildScriptletInjection(string hostname)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(hostname)) return null;

        var rules = ResolveScriptlets(hostname);
        if (rules.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.Append("(function(){try{\n");
        sb.Append(GetScriptletRuntime());
        sb.Append("\nvar __wimuxSL=window.__wimuxScriptlets;\n");
        foreach (var r in rules)
        {
            var argsJson = System.Text.Json.JsonSerializer.Serialize(r.Args);
            var nameJson = System.Text.Json.JsonSerializer.Serialize(r.Name);
            sb.Append("try{__wimuxSL.run(").Append(nameJson).Append(',').Append(argsJson).Append(");}catch(e){}\n");
        }
        sb.Append("}catch(e){}})();");
        return sb.ToString();
    }

    /// <summary>
    /// JS implementations of the supported uBlock scriptlets. Installed once as
    /// window.__wimuxScriptlets with a run(name, args) dispatcher. Focused on the
    /// response-pruning family that strips YouTube ad payloads
    /// (adPlacements / adSlots / playerAds) from player/get_watch responses.
    /// </summary>
    private static string GetScriptletRuntime()
    {
        return """
if (!window.__wimuxScriptlets) {
  var SL = window.__wimuxScriptlets = {};

  function parseList(s){ return (s||'').split(/[ \t]+/).filter(Boolean); }

  // Walk a dotted/space path list and delete matching keys from obj (in place).
  function pruneObject(obj, propsToPrune){
    if (!obj || typeof obj !== 'object') return false;
    var changed = false;
    propsToPrune.forEach(function(chain){
      var parts = chain.split('.');
      (function dig(node, idx){
        if (node == null || typeof node !== 'object') return;
        if (idx === parts.length - 1){
          if (Array.isArray(node)){
            node.forEach(function(it){ if (it && typeof it === 'object' && parts[idx] in it){ delete it[parts[idx]]; changed = true; } });
          } else if (parts[idx] in node){ delete node[parts[idx]]; changed = true; }
          return;
        }
        var key = parts[idx];
        if (key === '[]' && Array.isArray(node)){ node.forEach(function(it){ dig(it, idx+1); }); }
        else if (node[key] != null){ dig(node[key], idx+1); }
      })(obj, 0);
    });
    return changed;
  }

  function jsonPruneText(text, propsToPrune){
    try { var data = JSON.parse(text); pruneObject(data, propsToPrune); return JSON.stringify(data); }
    catch(e){ return text; }
  }

  function urlMatches(url, needle){
    if (!needle || needle === '*') return true;
    if (needle.length > 2 && needle[0] === '/' && needle[needle.length-1] === '/'){
      try { return new RegExp(needle.slice(1,-1)).test(url); } catch(e){ return false; }
    }
    // uBlock uses '?' loosely; treat the literal as a substring match.
    return url.indexOf(needle.replace(/\?$/,'')) !== -1;
  }

  // ── fetch() response interception ──────────────────────────────────────
  function hookFetch(transform){
    var orig = window.fetch;
    if (!orig || orig.__wimuxHooked) return;
    var hooked = function(input, init){
      var req = input instanceof Request ? input.url : String(input);
      return orig.apply(this, arguments).then(function(resp){
        try {
          var u = resp.url || req;
          var clone = resp.clone();
          return clone.text().then(function(body){
            var out = transform(u, body);
            if (out == null || out === body) return resp;
            return new Response(out, { status: resp.status, statusText: resp.statusText, headers: resp.headers });
          }).catch(function(){ return resp; });
        } catch(e){ return resp; }
      });
    };
    hooked.__wimuxHooked = true;
    window.fetch = hooked;
  }

  // ── XMLHttpRequest response interception ───────────────────────────────
  function hookXhr(transform){
    var OrigOpen = XMLHttpRequest.prototype.open;
    var OrigSend = XMLHttpRequest.prototype.send;
    if (OrigOpen.__wimuxHooked) return;
    var open = function(method, url){ this.__wimuxUrl = url; return OrigOpen.apply(this, arguments); };
    open.__wimuxHooked = true;
    XMLHttpRequest.prototype.open = open;
    XMLHttpRequest.prototype.send = function(){
      var xhr = this;
      var url = xhr.__wimuxUrl || '';
      xhr.addEventListener('readystatechange', function(){
        if (xhr.readyState === 4){
          try {
            if (xhr.responseType === '' || xhr.responseType === 'text'){
              var body = xhr.responseText;
              var out = transform(url, body);
              if (out != null && out !== body){
                Object.defineProperty(xhr, 'responseText', { value: out, configurable: true });
                Object.defineProperty(xhr, 'response', { value: out, configurable: true });
              }
            }
          } catch(e){}
        }
      });
      return OrigSend.apply(this, arguments);
    };
  }

  var fetchTransforms = [];
  var xhrTransforms = [];
  function ensureFetch(){ if (!ensureFetch.done){ ensureFetch.done = true; hookFetch(function(u,b){ var out=b; fetchTransforms.forEach(function(t){ out = t(u, out); }); return out; }); } }
  function ensureXhr(){ if (!ensureXhr.done){ ensureXhr.done = true; hookXhr(function(u,b){ var out=b; xhrTransforms.forEach(function(t){ out = t(u, out); }); return out; }); } }

  function replaceText(text, pattern, replacement){
    if (pattern.length > 2 && pattern[0] === '/' && pattern[pattern.length-1] === '/'){
      try { return text.replace(new RegExp(pattern.slice(1,-1),'g'), replacement); } catch(e){ return text; }
    }
    return text.split(pattern).join(replacement);
  }

  function setConstant(chain, value){
    var v = value === 'true' ? true : value === 'false' ? false :
            value === 'undefined' ? undefined : value === 'null' ? null :
            value === 'noopFunc' ? function(){} : value === 'emptyArr' ? [] :
            value === 'emptyObj' ? {} : /^-?\d+$/.test(value) ? parseInt(value,10) : value;
    var parts = chain.split('.');
    var owner = window;
    for (var i=0;i<parts.length-1;i++){ owner = owner[parts[i]] = owner[parts[i]] || {}; }
    try { Object.defineProperty(owner, parts[parts.length-1], { get:function(){return v;}, set:function(){}, configurable:false }); } catch(e){}
  }

  function noTimerIf(setter, needle, delay){
    var orig = window[setter];
    if (!orig) return;
    window[setter] = function(fn, t){
      try {
        var src = (typeof fn === 'function') ? fn.toString() : String(fn);
        var matchNeedle = !needle || needle === '*' ||
          (needle.length>2 && needle[0]==='/' && needle[needle.length-1]==='/' ? new RegExp(needle.slice(1,-1)).test(src) : src.indexOf(needle)!==-1);
        var matchDelay = (delay === undefined || delay === '' || delay === '*') ? true : String(t) === String(delay);
        if (matchNeedle && matchDelay) return 0;
      } catch(e){}
      return orig.apply(this, arguments);
    };
  }

  SL.run = function(name, args){
    switch(name){
      case 'json-prune-fetch-response': {
        var props = parseList(args[0]); ensureFetch();
        fetchTransforms.push(function(u,b){ return props.length ? jsonPruneText(b, props) : b; });
        break;
      }
      case 'json-prune-xhr-response': {
        var props = parseList(args[0]); ensureXhr();
        xhrTransforms.push(function(u,b){ return props.length ? jsonPruneText(b, props) : b; });
        break;
      }
      case 'json-prune': {
        // Static page-data prune: target ytInitialPlayerResponse style globals.
        var props = parseList(args[0]);
        ['ytInitialPlayerResponse','ytInitialData'].forEach(function(g){
          try { if (window[g]) pruneObject(window[g], props); } catch(e){}
        });
        break;
      }
      case 'trusted-replace-fetch-response': {
        var pat = args[0]||'', rep = args[1]||'', cond = args[2]||''; ensureFetch();
        fetchTransforms.push(function(u,b){ return (!cond || urlMatches(u,cond)) ? replaceText(b, pat, rep) : b; });
        break;
      }
      case 'trusted-replace-xhr-response': {
        var pat = args[0]||'', rep = args[1]||'', cond = args[2]||''; ensureXhr();
        xhrTransforms.push(function(u,b){ return (!cond || urlMatches(u,cond)) ? replaceText(b, pat, rep) : b; });
        break;
      }
      case 'set-constant': { if (args[0]) setConstant(args[0], args[1]); break; }
      case 'no-setInterval-if': { noTimerIf('setInterval', args[0]||'', args[1]); break; }
      case 'no-setTimeout-if': { noTimerIf('setTimeout', args[0]||'', args[1]); break; }
    }
  };
}
""";
    }
}
