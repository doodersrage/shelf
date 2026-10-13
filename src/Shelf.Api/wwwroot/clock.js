// The browser's own time zone, by its IANA name, so a time typed on a page means the same moment to the server.
export const zone = () => Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
