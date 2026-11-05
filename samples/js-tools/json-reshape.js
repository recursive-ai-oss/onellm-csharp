// A tool file that describes itself: the exported `tool` object is the description.
export const tool = {
  name: "reshape_json",
  description: "Picks fields out of a list of JSON objects and renames them. 'fields' maps new names to dotted paths, for example {\"city\": \"address.city\"}.",
  inputSchema: {
    type: "object",
    properties: {
      items: { type: "array", items: { type: "object" }, description: "The objects to reshape." },
      fields: { type: "object", additionalProperties: { type: "string" }, description: "New field name to dotted path." },
    },
    required: ["items", "fields"],
    additionalProperties: false,
  },
};

const pick = (obj, path) => path.split(".").reduce((o, k) => (o == null ? undefined : o[k]), obj);

export function invoke({ items, fields }) {
  return items.map((item) => {
    const out = {};
    for (const [name, path] of Object.entries(fields)) {
      const v = pick(item, path);
      if (v !== undefined) out[name] = v;
    }
    return out;
  });
}
