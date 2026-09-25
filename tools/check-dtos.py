#!/usr/bin/env python3
"""Compares the server's wire DTOs against the client's hand-mirrored copies.

The two halves target different frameworks (net10.0 on the server, netstandard2.1 in the Unity
plugin), so they cannot share a source file: every wire type in
Source/Tarkov-QuestTree-Server/QuestTreeDtos.cs and ZoneHarvestDtos.cs is hand-copied into
Source/Tarkov-QuestTree/QuestGraph/. Drift between the two has already produced real bugs - a field
the server sends and the client silently drops reads as a default, not as an error, and the
SchemaVersion/SupportedSchemaVersion pair only catches a bump somebody remembered to make. Nothing
checked the mirrors themselves until this script.

What it does: parses the class/property declarations on both sides (server wire names come from
WireJson's camelCase policy, client names from [JsonProperty] where present), matches server type to
client type through the table below, and reports a field one side has and the other does not, plus
collection-vs-scalar type disagreements.

What it does NOT catch, by design:
  - nested or aliased types it cannot resolve: it compares wire NAMES and collection-ness, not the
    shape of a List<T>'s T beyond T being mirrored somewhere in its own right;
  - enum values, string vocabularies ("fitted"/"swap"/"add"), units, nullability, defaults;
  - semantics - two fields can agree perfectly on the wire and mean different things (the reason
    ObjectiveDto.IsNecessary and every "Known" flag carry paragraphs of comment);
  - anything the server writes through SPT's JsonUtil rather than WireJson (ItemsJson's contents),
    and whether either side actually READS the field it declares.

Proven able to fail before it shipped: a bogus property added to the client's RewardDto made it exit
1 naming the class and the property; see the report in the commit that added it.

Usage:  python tools/check-dtos.py          compare, print the table, exit 1 on drift
"""

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent  # tools/ sits one level below the repo root
SERVER = REPO / "Source" / "Tarkov-QuestTree-Server"
CLIENT = REPO / "Source" / "Tarkov-QuestTree" / "QuestGraph"

SERVER_FILES = [SERVER / "QuestTreeDtos.cs", SERVER / "ZoneHarvestDtos.cs"]
CLIENT_FILES = [
    CLIENT / "QuestDto.cs",
    CLIENT / "ProfileBuildsDto.cs",
    CLIENT / "RaidCheckDto.cs",
    CLIENT / "ZoneHarvestDto.cs",
    CLIENT / "MapTransferDto.cs",
    CLIENT / "QuestDataClient.cs",  # holds the nested SavePresetResult
    CLIENT / "KappaFetchResult.cs",  # a wrapper AROUND KappaPayloadDto, not a mirror of anything
]

# Server wire types the client mirrors under the SAME name - almost all of them.
SAME_NAME = """
    QuestPayloadDto DerivedLocationDto QuestDto PrerequisiteDto ObjectiveDto RewardDto
    KappaPayloadDto KappaItemDto ProfilePayloadDto HeldItemDto TraderStateDto LockReasonDto
    WeaponBuildDto WeaponModelCheckDto SolvedBuildDto SolvedPartDto WeaponBuildThresholdDto
    ProfileBuildsDto ProfileBuildDto ProfilePartDto
    RaidCheckDto RaidCheckMapDto RaidCheckRequirementDto
    MapMarkerPayloadDto MapMarkerSetDto MapMarkerDto
    ZoneHarvestRequest HarvestedTrigger HarvestedQuestItem ZoneHarvestResponse
    MapExtentDto MapFloorDto
    MapRectDto MapCaptureFloorDto MapLabelDto MapCaptureMetaDto MapCaptureMeshDto MapCaptureSideDto
    MapCaptureAtlasDto
    MapUploadRequest MapUploadResponse MapIndexEntryDto MapIndexDto MapImageRequest MapImageDto
    MapMeshUploadRequest MapMeshUploadResponse MapMeshRequest MapMeshDto
""".split()

# The ones whose client copy carries a different name. server type -> client type.
ALIASES = {
    # The answer to /questtree/build/save, in QuestDataClient. The client calls it Result because
    # that is what it is there - what happened, to be shown to the player - not an HTTP response.
    "SavePresetResponse": "SavePresetResult",
}

MIRRORS = dict({name: name for name in SAME_NAME}, **ALIASES)

# Server wire types with no client mirror, and why that is fine rather than an omission.
NO_MIRROR = {
    "SavePresetRequest": "request body the client WRITES, never reads - QuestDataClient.SavePreset "
                         "serialises an anonymous { key, clientVersion }, so there is no mirror to drift. "
                         "Its two names are pinned with JsonPropertyName on the server side.",
    "ZoneFile": "on-disk only: what ZoneStore writes per map under zones\\. Never on the wire, and the "
                "client never reads a zone file.",
}

# Client properties that are deliberately absent from the server, keyed (client class, wire name),
# with the reason. Empty today; a field kept for an older server would be recorded here rather than
# silencing the check.
CLIENT_ONLY_OK = {}

CLASS_RE = re.compile(
    r"\b(?:public|internal|private|protected)\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+)*class\s+(\w+)")
PROP_RE = re.compile(
    r"((?:\[[^\]]*\]\s*)*)public\s+([A-Za-z0-9_<>,.?\[\]\s]+?)\s+(\w+)\s*\{\s*get;\s*set;\s*\}")
JSON_NAME_RE = re.compile(r"\[Json(?:Property|PropertyName)\(\s*\"([^\"]+)\"")
COLLECTION_RE = re.compile(
    r"^(List|IList|IReadOnlyList|IEnumerable|ICollection|Dictionary|IReadOnlyDictionary|HashSet)\s*<")


def scrub(text):
    """(code, masked), both the input's length so offsets are interchangeable: code has comments
    blanked and string literals intact, so attribute names survive; masked also blanks literal
    contents, so a brace inside a string or a comment cannot throw off the brace matching."""
    code, masked = [], []
    i, n = 0, len(text)
    while i < n:
        c, nxt = text[i], text[i + 1] if i + 1 < n else ""
        if c + nxt == "//":
            while i < n and text[i] != "\n":
                code.append(" "), masked.append(" ")
                i += 1
        elif c + nxt == "/*":
            while i < n and text[i - 1:i + 1] != "*/":
                ch = "\n" if text[i] == "\n" else " "
                code.append(ch), masked.append(ch)
                i += 1
        elif c in "\"'":
            quote, code_run, mask_run = c, [c], [c]
            i += 1
            while i < n and text[i] != quote:
                if text[i] == "\\" and i + 1 < n:
                    code_run.append(text[i]), mask_run.append("x")
                    i += 1
                code_run.append(text[i])
                mask_run.append("\n" if text[i] == "\n" else "x")
                i += 1
            if i < n:
                code_run.append(quote), mask_run.append(quote)
                i += 1
            code.extend(code_run), masked.extend(mask_run)
        else:
            code.append(c), masked.append(c)
            i += 1
    return "".join(code), "".join(masked)


def parse(paths):
    """{class name: [(property, wire name, type)]} across the given files, innermost class wins."""
    found = {}
    for path in paths:
        if not path.exists():
            fail_hard(f"missing DTO file: {path}")
        code, masked = scrub(path.read_text(encoding="utf-8"))

        spans = []  # (name, body start, body end)
        for m in CLASS_RE.finditer(code):
            start = masked.find("{", m.end())
            if start < 0:
                continue
            depth, end = 0, len(masked) - 1
            for i in range(start, len(masked)):
                if masked[i] == "{":
                    depth += 1
                elif masked[i] == "}":
                    depth -= 1
                    if depth == 0:
                        end = i
                        break
            spans.append((m.group(1), start, end))
            found.setdefault(m.group(1), [])

        for p in PROP_RE.finditer(code):
            attrs, ctype, name = p.group(1), " ".join(p.group(2).split()), p.group(3)
            if "JsonIgnore" in attrs:
                continue
            inner = None
            for cls, start, end in spans:
                if start < p.start() < end and (inner is None or start > inner[1]):
                    inner = (cls, start)
            if inner is None:
                continue
            named = JSON_NAME_RE.search(attrs)
            wire = named.group(1) if named else name[:1].lower() + name[1:]
            found[inner[0]].append((name, wire, ctype))
    return found


def is_collection(ctype):
    bare = ctype.rstrip("?").strip()
    return bare.endswith("[]") or COLLECTION_RE.match(bare) is not None


def fail_hard(message):
    print(f"DTO CHECK FAILED: {message}")
    sys.exit(1)


def main():
    server, client = parse(SERVER_FILES), parse(CLIENT_FILES)
    errors, warnings, compared = [], [], 0

    print("server wire type                mirrored by")
    print("-" * 78)
    for cls in sorted(server):
        if cls in NO_MIRROR:
            print(f"  {cls:<30}(none) {NO_MIRROR[cls]}")
        elif cls not in MIRRORS:
            print(f"  {cls:<30}UNKNOWN")
            errors.append(f"{cls}: a server wire type this check has never seen - mirror it on the "
                          f"client and add it to MIRRORS, or record it in NO_MIRROR with the reason")
        else:
            print(f"  {cls:<30}{MIRRORS[cls]}")
    print()

    for scls, ccls in MIRRORS.items():
        if scls not in server:
            errors.append(f"{scls}: named in MIRRORS but no longer declared on the server - was it "
                          f"renamed? Update MIRRORS.")
            continue
        if ccls not in client:
            errors.append(f"{scls}: no client mirror - {ccls} is not declared in any of "
                          f"{', '.join(p.name for p in CLIENT_FILES)}")
            continue
        compared += 1
        sprops = {wire: (name, ctype) for name, wire, ctype in server[scls]}
        cprops = {wire: (name, ctype) for name, wire, ctype in client[ccls]}

        for wire in sorted(set(sprops) - set(cprops)):
            errors.append(f"{scls}.{sprops[wire][0]} (wire \"{wire}\") is sent by the server and "
                          f"MISSING from client {ccls} - the client drops it silently")
        for wire in sorted(set(cprops) - set(sprops)):
            if (ccls, wire) in CLIENT_ONLY_OK:
                continue
            errors.append(f"{ccls}.{cprops[wire][0]} (wire \"{wire}\") is read by the client and "
                          f"NEVER SENT by server {scls} - it will always be the default. If that is "
                          f"deliberate, record it in CLIENT_ONLY_OK with the reason")
        for wire in sorted(set(sprops) & set(cprops)):
            stype, ctype = sprops[wire][1], cprops[wire][1]
            if is_collection(stype) != is_collection(ctype):
                warnings.append(f"{scls}.{sprops[wire][0]}: server {stype} vs client {ctype} - one "
                                f"is a collection and the other is not")

    unmapped = sorted(set(client) - set(MIRRORS.values()))
    if unmapped:
        print("client types with no server counterpart (helpers, not wire types):")
        for cls in unmapped:
            print(f"  {cls}")
        print()

    for w in warnings:
        print(f"WARN   {w}")
    for e in errors:
        print(f"ERROR  {e}")

    if errors:
        print()
        print(f"DTO CHECK FAILED: {len(errors)} drift(s) between the server DTOs and the client "
              f"mirrors, {len(warnings)} warning(s).")
        return 1

    print(f"{compared} wire types compared, 0 drift"
          + (f" ({len(warnings)} warning(s))" if warnings else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
