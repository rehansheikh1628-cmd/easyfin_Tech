// Development environment configuration
// Public to the client browser — NEVER store private secrets here.
export const environment = {
  production: false,
  // Empty string uses relative paths (e.g. '/api/statements'), delegating to proxy.conf.js in development
  apiUrl: ''
};
