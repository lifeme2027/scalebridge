// /box 的安全响应头：本地服务器、构建检查与（文字上）ASP.NET 端的 BoxSite 保持同一份定义。
export const BOX_CSP = [
  "default-src 'self'",
  "script-src 'self'",
  "connect-src 'self'",
  "style-src 'self'",
  "img-src 'self' data:",
  "object-src 'none'",
  "base-uri 'none'",
  "form-action 'none'",
  "frame-ancestors 'none'",
].join('; ');

// 只给 /box 开 bluetooth；其余沿用站点的默认关闭。
export const BOX_PERMISSIONS_POLICY = 'bluetooth=(self), camera=(), microphone=(), geolocation=(), payment=(), usb=()';
