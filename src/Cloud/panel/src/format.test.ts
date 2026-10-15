import { describe, expect, it } from "vitest";
import { addDays, ago, cashierLabel, isStale, money, storeDay } from "./format";

describe("format", () => {
  it("writes amounts like the register: RD$ 1,234.50", () => {
    expect(money(1234.5)).toBe("RD$ 1,234.50");
    expect(money(0)).toBe("RD$ 0.00");
    expect(money(1_000_000)).toBe("RD$ 1,000,000.00");
    expect(money(-200)).toBe("-RD$ 200.00");
  });

  it("uses the store day (UTC−4), not the UTC day", () => {
    expect(storeDay(new Date("2026-10-16T03:30:00Z"))).toBe("2026-10-15");
    expect(storeDay(new Date("2026-10-16T04:00:00Z"))).toBe("2026-10-16");
    expect(addDays("2026-10-01", -1)).toBe("2026-09-30");
  });

  it("says how long ago and flags 24 h without data", () => {
    const now = Date.parse("2026-10-15T12:00:00Z");
    expect(ago("2026-10-15T11:55:00Z", now)).toBe("hace 5 min");
    expect(ago("2026-10-15T09:00:00Z", now)).toBe("hace 3 h");
    expect(ago("2026-10-12T12:00:00Z", now)).toBe("hace 3 días");
    expect(isStale("2026-10-14T13:00:00Z", now)).toBe(false);
    expect(isStale("2026-10-14T11:00:00Z", now)).toBe(true);
    expect(isStale(null, now)).toBe(true);
  });

  it("names a cashier by name, or by the end of the id until the store sends it", () => {
    expect(cashierLabel("0199e3a1-0000-7000-8000-00000000a1b2", "Yudelka Martínez")).toBe("Yudelka Martínez");
    expect(cashierLabel("0199e3a1-0000-7000-8000-00000000a1b2", null)).toBe("Cajero …a1b2");
  });
});
