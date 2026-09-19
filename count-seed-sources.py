"""Counts, over the shipped weapon-build seed, how many part instances a trader sells for cash,
how many only the flea market would (handbook-priced, no trader), and how many have no price.

The go/no-go for 1.18.0's objective change: the flea-only count must FALL against the seed shipped
under solver 10 (48 of 609 instances on this install's SPT_Data, 2026-09-19). Reads SPT_Data only -
mod-injected parts show as unpriced here, which is why the figure differs from the server's own
boot line. Same rules as PartPrices.Shared: root offers, cheapest single-currency scheme, Fence
excluded, and dollars, euros AND GP COINS converted at the handbook rate.

The GP coin is the one to keep in step by hand. It is money to SPT - Money.GetMoneyTpls() lists four
currencies, not three - and all 156 of Ref's cash offers are priced in it, so leaving it out of the
rate map below reports every Ref-only part as flea-only and overstates the baseline by three. The
Lega Medal is deliberately absent: the game does not treat it as currency, so Ref's other 11 offers
are barters, which is what they are.

    python count-seed-sources.py [seed-path]
"""
import collections
import json
import os
import sys

D = r"C:\Games\SPT\SPT_Runtime\SPT_Data\database"
SEED = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "Source", "Tarkov-QuestTree-Server", "weapon-builds.json")
FENCE = "579dc571d53a0658a154fbec"

hb = {i["Id"]: i["Price"] for i in json.load(open(os.path.join(D, "templates", "handbook.json"), encoding="utf-8"))["Items"]}
rate = {
    "5449016a4bdc2d6f028b456f": 1.0,
    "5696686a4bdc2da3298b456a": float(hb.get("5696686a4bdc2da3298b456a") or 0),  # dollars
    "569668774bdc2da2298b4568": float(hb.get("569668774bdc2da2298b4568") or 0),  # euros
    "5d235b4d86f7742e017bc88a": float(hb.get("5d235b4d86f7742e017bc88a") or 0),  # GP coin, Ref's only price
}

trader = {}
for d in os.listdir(os.path.join(D, "traders")):
    p = os.path.join(D, "traders", d, "assort.json")
    if d == FENCE or not os.path.exists(p):
        continue
    a = json.load(open(p, encoding="utf-8"))
    bs = a.get("barter_scheme") or {}
    for it in a.get("items") or []:
        if it.get("parentId") != "hideout":   # the field PartPrices.IsRootOffer tests
            continue
        cash = [round((s[0].get("count") or 0) * rate[s[0]["_tpl"]]) for s in bs.get(it["_id"]) or []
                if len(s) == 1 and rate.get(s[0].get("_tpl"))]
        cash = [c for c in cash if c > 0]
        if cash:
            trader[it["_tpl"]] = min([min(cash)] + ([trader[it["_tpl"]]] if it["_tpl"] in trader else []))

seed = json.load(open(SEED, encoding="utf-8"))
c = collections.Counter(
    # A handbook price of 0 is NOT a price - WeaponSolver.Priced counts only `> 0` - so those
    # 168 templates land in "unpriced", the same place the mod puts them.
    "trader" if p["Template"] in trader else "flea-only" if (hb.get(p["Template"]) or 0) > 0 else "unpriced"
    for b in seed["Builds"].values() for p in b["Parts"])
print("solver", seed["SolverVersion"], "builds", len(seed["Builds"]), "instances", sum(c.values()), dict(c))
