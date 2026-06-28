type IconProps = { className?: string; size?: number };

function svgProps({ className, size = 14 }: IconProps) {
  return { className, width: size, height: size, viewBox: "0 0 16 16", fill: "none", "aria-hidden": true };
}

export function SearchIcon(props: IconProps) {
  return <svg {...svgProps(props)}><circle cx="7" cy="7" r="4.5" stroke="currentColor" /><path d="M10.5 10.5L14 14" stroke="currentColor" strokeLinecap="round" /></svg>;
}

export function PlusIcon(props: IconProps) {
  return <svg {...svgProps(props)}><path d="M8 3V13M3 8H13" stroke="currentColor" strokeLinecap="round" /></svg>;
}

export function XIcon(props: IconProps) {
  return <svg {...svgProps(props)}><path d="M4.5 4.5L11.5 11.5M11.5 4.5L4.5 11.5" stroke="currentColor" strokeLinecap="round" /></svg>;
}

export function TrashIcon(props: IconProps) {
  return <svg {...svgProps(props)}><path d="M3 4H13M6 4V3H10V4M5 6V13H11V6" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" /></svg>;
}

export function RefreshIcon(props: IconProps) {
  return <svg {...svgProps(props)}><path d="M13 5V2.5H10.5M12.6 5A5 5 0 1 0 13 10" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" /></svg>;
}

export function BellIcon(props: IconProps) {
  return <svg {...svgProps(props)}><path d="M5 12.5H11M6.2 13C6.5 13.8 7.1 14.2 8 14.2C8.9 14.2 9.5 13.8 9.8 13M4.5 11.5V7C4.5 4.9 5.8 3.4 8 3.4C10.2 3.4 11.5 4.9 11.5 7V11.5L12.5 12.5H3.5L4.5 11.5Z" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" /></svg>;
}

export function ImageIcon(props: IconProps) {
  return <svg {...svgProps(props)}><rect x="2.5" y="3" width="11" height="10" rx="1.5" stroke="currentColor" /><circle cx="6" cy="6.5" r="1.2" fill="currentColor" /><path d="M3 12L6.2 9L8.2 10.7L10.4 8.4L13 11.2" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" /></svg>;
}

export function FileIcon(props: IconProps) {
  return <svg {...svgProps(props)}><path d="M4 2.5H9.5L12 5V13.5H4V2.5Z" stroke="currentColor" strokeLinejoin="round" /><path d="M9.5 2.5V5H12M6 8H10M6 10.5H10" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" /></svg>;
}

export function ArrowUpIcon(props: IconProps) {
  return <svg {...svgProps(props)}><path d="M8 13V3M4.5 6.5L8 3L11.5 6.5" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" /></svg>;
}

export function ChevronDownIcon(props: IconProps) {
  return <svg {...svgProps(props)}><path d="M4 6L8 10L12 6" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" /></svg>;
}

export function ChevronRightIcon(props: IconProps) {
  return <svg {...svgProps(props)}><path d="M6 4L10 8L6 12" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" /></svg>;
}

export function FolderIcon(props: IconProps) {
  return <svg {...svgProps(props)}><path d="M2.5 5.5H6L7.2 4.2H13.5V12.5H2.5V5.5Z" stroke="currentColor" strokeLinejoin="round" /></svg>;
}

export function DownloadIcon(props: IconProps) {
  return <svg {...svgProps(props)}><path d="M8 2.5V10M5 7L8 10L11 7M3.5 13H12.5" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" /></svg>;
}

export function RocketIcon(props: IconProps) {
  return <svg {...svgProps(props)}><path d="M9.6 2.5C11.3 2.7 12.6 3.9 12.8 5.6L9.2 9.2L6.8 6.8L9.6 2.5Z" stroke="currentColor" strokeLinejoin="round" /><path d="M6.8 6.8L4.3 6.4L3 7.7L5.8 8.2M9.2 9.2L9.6 11.7L8.3 13L7.8 10.2M5.2 10.8L3.5 12.5" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" /></svg>;
}
