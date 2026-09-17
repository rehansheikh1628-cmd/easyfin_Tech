// Production environment configuration
// Public to the client browser — NEVER store private secrets here.
export const environment = {
  production: true,
  apiUrl: '',
  // Production API URL placeholder — when deployed as a unified SPA (served from wwwroot),
  // leave as '' to use same-origin relative URLs. If deploying as a separate frontend domain,
  // configure the production backend API domain here.
};
