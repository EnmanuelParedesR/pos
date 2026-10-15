/** A fake fetch response with the parts the API wrapper reads (Node 16 has no global Response). */
export const json = (status: number, body?: unknown) =>
  ({
    ok: status >= 200 && status < 300,
    status,
    json: async () => (body === undefined ? Promise.reject(new SyntaxError("empty")) : body),
    text: async () => (body === undefined ? "" : JSON.stringify(body)),
  }) as Response;
