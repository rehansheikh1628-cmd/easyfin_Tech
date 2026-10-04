const { env } = require('process');

// Resolve proxy target from environment (set by ASP.NET Core launch profile)
// Priority: explicit HTTPS port > ASPNETCORE_URLS > HTTP port > HTTPS default
const target =
  env.ASPNETCORE_HTTPS_PORT
    ? `https://localhost:${env.ASPNETCORE_HTTPS_PORT}`
    : env.ASPNETCORE_URLS
      ? env.ASPNETCORE_URLS.split(';')[0]
      : env.ASPNETCORE_HTTP_PORTS
        ? `http://localhost:${env.ASPNETCORE_HTTP_PORTS}`
        : 'http://localhost:5011';

const PROXY_CONFIG = [
  {
    context: [
      "/api",
      "/swagger",
      "/openapi"
    ],
    target,
    secure: false
  }
]

module.exports = PROXY_CONFIG;
