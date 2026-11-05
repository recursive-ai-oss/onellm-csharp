// A tool file: the description sits beside it in unit-converter.tool.yaml, and invoke() does the work.
const toMetres = { m: 1, km: 1000, cm: 0.01, mm: 0.001, mi: 1609.344, yd: 0.9144, ft: 0.3048, in: 0.0254 };
const toGrams = { g: 1, kg: 1000, mg: 0.001, lb: 453.59237, oz: 28.349523125 };

function table(unit) {
  if (unit in toMetres) return ["length", toMetres];
  if (unit in toGrams) return ["mass", toGrams];
  return null;
}

export function invoke(inputs) {
  const { value, from, to } = inputs;
  if (from === "c" || from === "f" || from === "k") {
    const kelvin = from === "c" ? value + 273.15 : from === "f" ? (value - 32) * 5 / 9 + 273.15 : value;
    const out = to === "c" ? kelvin - 273.15 : to === "f" ? (kelvin - 273.15) * 9 / 5 + 32 : to === "k" ? kelvin : NaN;
    if (Number.isNaN(out)) throw new Error(`cannot convert temperature to '${to}'; use c, f or k`);
    return { value: Math.round(out * 1e6) / 1e6, unit: to };
  }
  const a = table(from), b = table(to);
  if (!a || !b || a[0] !== b[0]) throw new Error(`cannot convert '${from}' to '${to}'`);
  return { value: Math.round(value * a[1][from] / b[1][to] * 1e9) / 1e9, unit: to };
}
