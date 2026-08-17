// Baseline development copy. CI regenerates this file from the running ASP.NET OpenAPI document
// before every controlled frontend build.
export interface paths {
  "/api/system/info": {
    parameters: { query?: never; header?: never; path?: never; cookie?: never };
    get: {
      responses: {
        200: {
          content: {
            "application/json": {
              referenceImplementation?: string;
              baseline?: string;
              activeBatch?: string;
              status?: string;
              metricsMeter?: string;
            };
          };
        };
      };
    };
  };
}
