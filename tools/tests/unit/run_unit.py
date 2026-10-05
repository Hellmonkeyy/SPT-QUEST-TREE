"""Layer C: unit tests on the SHIPPING code's pure logic.

Generalises tools/check-relief-simplify.py. Every region in Source/Tarkov-QuestTree and Source/Tarkov-QuestTree-Server between

    // BEGIN TESTABLE <Suite>[.<Part>]
    ...
    // END TESTABLE <Suite>[.<Part>]

is extracted (regions of the same full name are joined in file order) and pasted into its harness
tools/tests/unit/<Suite>Tests.cs, which says where with these comment lines:

    // @@REGION <Suite>[.<Part>]@@                  the region's text goes here
    // @@CONST <source path> <Name>@@               the ONE `const ... <Name> = ...;` line of that source file
    // @@MUTATE <Suite>[.<Part>] :: <old> :: <new>@@  for --self-test: <old> must occur exactly once in that region

All harnesses, UnityShims.cs (Unity's Vector2/3, Bounds, Mathf with Unity's semantics) and UnitCommon.cs (the case
runner) are compiled into one temporary console project and run; each case prints one RESULT line.

Every region must have a harness that uses it, every placeholder must find its region or constant, every suite must run
at least one case, and a case that asserts nothing fails - so a check cannot pass by not running.

--self-test builds a second project in which each mutation is a separate copy of its suite (namespace
UnitTests.<Suite>__m<k>) with that one substitution applied to the extracted region: every mutation must make at least
one case of its suite FAIL, and every suite must have a mutation. tools/check-relief-simplify.py (which carries its own
must-fail variants) is run as one more result in both modes, unless --no-relief.

API for tools/run-tests.py:  run(ctx) -> list[Result], Result = (layer, name, status, detail), status PASS/FAIL/SKIP.
ctx may be None, a dict or an object; it is read for `self_test` (bool), `relief` (bool, default True), `verbose`.

Usage: python tools/tests/unit/run_unit.py [--self-test] [--no-relief] [-v]   (exit 0 = no FAIL)
"""
import collections
import glob
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
SOURCE_DIRS = [os.path.join(ROOT, 'Source', d) for d in ('Tarkov-QuestTree', 'Tarkov-QuestTree-Server')]  # client, server
RELIEF = os.path.join(ROOT, 'tools', 'check-relief-simplify.py')
LAYER = 'C'

Result = collections.namedtuple('Result', 'layer name status detail')

BEGIN = re.compile(r'^\s*// BEGIN TESTABLE ([A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)?)\b')
END = re.compile(r'^\s*// END TESTABLE ([A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)?)\b')
REGION_REF = re.compile(r'^[ \t]*// @@REGION (\S+)@@[ \t]*$', re.M)
CONST_REF = re.compile(r'^[ \t]*// @@CONST (\S+) (\w+)@@[ \t]*$', re.M)
MUTATE_REF = re.compile(r'^[ \t]*// @@MUTATE (\S+) :: (.*?) :: (.*?)@@[ \t]*$', re.M)
NAMESPACE = 'namespace UnitTests.%s'


class Broken(Exception):
    """A harness or region that cannot be assembled - reported as a FAIL, never skipped."""


def _opt(ctx, name, default):
    if ctx is None:
        return default
    if isinstance(ctx, dict):
        return ctx.get(name, default)
    return getattr(ctx, name, default)


# --- extraction -----------------------------------------------------------------------------------------------------

def regions():
    """{full name: [(file, first line, text)]} for every TESTABLE region under Source/Tarkov-QuestTree and
    Source/Tarkov-QuestTree-Server; a name used in both projects is an error, not a merge."""
    found = collections.OrderedDict()
    tree_of = {}
    for path in [p for d in SOURCE_DIRS for p in sorted(glob.glob(os.path.join(d, '**', '*.cs'), recursive=True))]:
        rel = os.path.relpath(path, ROOT)
        tree = rel.split(os.sep)[1]
        if os.sep + 'obj' + os.sep in path or os.sep + 'bin' + os.sep in path:
            continue
        with open(path, encoding='utf-8-sig') as f:
            lines = f.read().split('\n')
        open_name, start, body = None, 0, []
        for number, line in enumerate(lines, 1):
            b, e = BEGIN.match(line), END.match(line)
            if b:
                if open_name:
                    raise Broken('%s:%d: BEGIN TESTABLE %s inside %s' % (rel, number, b.group(1), open_name))
                open_name, start, body = b.group(1), number, []
            elif e:
                if e.group(1) != open_name:
                    raise Broken('%s:%d: END TESTABLE %s does not close %s' % (rel, number, e.group(1), open_name))
                if tree_of.setdefault(open_name, tree) != tree:
                    raise Broken('%s:%d: region %s is also in %s - a name must be in one project only'
                                 % (rel, start, open_name, tree_of[open_name]))
                found.setdefault(open_name, []).append((rel, start, '\n'.join(body)))
                open_name = None
            elif open_name:
                body.append(line)
        if open_name:
            raise Broken('%s:%d: BEGIN TESTABLE %s never ends' % (rel, start, open_name))
    return found


def suite_of(name):
    return name.split('.')[0]


def constant(rel, name):
    path = os.path.join(ROOT, rel)
    if not os.path.isfile(path):
        raise Broken('@@CONST: no file %s' % rel)
    with open(path, encoding='utf-8-sig') as f:
        text = f.read()
    hits = re.findall(r'^[ \t]*((?:(?:public|internal|private|protected)\s+)*const\s+[\w.]+\s+%s\s*=[^;\n]+;)' % re.escape(name),
                      text, re.M)
    if len(hits) != 1:
        raise Broken('@@CONST %s: %d declarations in %s, need exactly 1' % (name, len(hits), rel))
    return '        ' + hits[0]


def assemble(suite, harness, found, mutation=None, tag=None):
    """The harness text with its placeholders filled; `mutation` = (region, old, new) applied to that region;
    `tag` renames the suite's namespace so a mutant can sit beside other copies."""
    def region_text(match):
        name = match.group(1)
        if suite_of(name) != suite:
            raise Broken('%sTests.cs pastes region %s of another suite' % (suite, name))
        if name not in found:
            raise Broken('%sTests.cs: no region "BEGIN TESTABLE %s" in the source' % (suite, name))
        text = '\n'.join(t for _, _, t in found[name])
        if mutation and mutation[0] == name:
            count = text.count(mutation[1])
            if count != 1:
                raise Broken('mutation "%s" occurs %d times in region %s, need exactly 1' % (mutation[1], count, name))
            text = text.replace(mutation[1], mutation[2])
        return text

    text = REGION_REF.sub(lambda m: region_text(m), harness)
    text = CONST_REF.sub(lambda m: constant(m.group(1), m.group(2)), text)
    if mutation and not any(m.group(1) == mutation[0] for m in REGION_REF.finditer(harness)):
        raise Broken('mutation names region %s, which %sTests.cs does not paste' % (mutation[0], suite))
    if tag:
        if (NAMESPACE % suite) not in text:
            raise Broken('%sTests.cs does not declare "%s"' % (suite, NAMESPACE % suite))
        text = re.sub(r'\bUnitTests\.%s\b' % re.escape(suite), 'UnitTests.%s__%s' % (suite, tag), text)
    return text


def harnesses(found):
    """{suite: harness text}, checked both ways against the regions."""
    files = {os.path.basename(p)[:-len('Tests.cs')]: p for p in glob.glob(os.path.join(HERE, '*Tests.cs'))}
    problems = []
    for name in found:
        if suite_of(name) not in files:
            problems.append('region %s has no harness tools/tests/unit/%sTests.cs' % (name, suite_of(name)))
    texts = {}
    for suite, path in sorted(files.items()):
        with open(path, encoding='utf-8-sig') as f:
            texts[suite] = f.read()
        used = set(m.group(1) for m in REGION_REF.finditer(texts[suite]))
        for name in found:
            if suite_of(name) == suite and name not in used:
                problems.append('region %s is not pasted by %sTests.cs' % (name, suite))
    return texts, problems


# --- build and run --------------------------------------------------------------------------------------------------

def framework():
    try:
        out = subprocess.run(['dotnet', '--list-sdks'], capture_output=True, text=True).stdout
    except OSError:
        raise Broken('dotnet is not on PATH')
    majors = [int(m) for m in re.findall(r'^(\d+)\.', out, re.M)]
    if not majors:
        raise Broken('dotnet --list-sdks lists no SDK')
    return 'net%d.0' % max(majors)


PROJECT = '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>%s</TargetFramework><Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings><TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <AssemblyName>unit</AssemblyName><RootNamespace>UnitTests</RootNamespace><NoWarn>CS0169;CS0414;CS0649;CS1591;CS8981</NoWarn>
  </PropertyGroup>
</Project>
'''


def build_and_run(folder, sources, tfm, verbose):
    """Writes the project, builds it, runs it; returns {(suite, case): (status, detail)} in order, or raises Broken."""
    os.makedirs(folder)
    with open(os.path.join(folder, 'unit.csproj'), 'w') as f:
        f.write(PROJECT % tfm)
    for name, text in sources.items():
        with open(os.path.join(folder, name), 'w', encoding='utf-8') as f:
            f.write(text)
    build = subprocess.run(['dotnet', 'build', folder, '-c', 'Release', '-nologo', '-v', 'q', '-clp:NoSummary;ErrorsOnly'],
                           capture_output=True, text=True)
    if build.returncode != 0:
        errors = [l.strip() for l in (build.stdout + build.stderr).splitlines() if 'error' in l]
        raise Broken('harness build failed: ' + ' | '.join(errors[:8]))
    dll = glob.glob(os.path.join(folder, 'bin', 'Release', tfm, 'unit.dll'))
    if not dll:
        raise Broken('built, but no unit.dll')
    run = subprocess.run(['dotnet', dll[0]], capture_output=True, text=True)
    if verbose:
        print(run.stdout)
    if run.returncode != 0:
        raise Broken('harness exited %d: %s' % (run.returncode, (run.stderr or run.stdout)[-1500:]))
    results = collections.OrderedDict()
    for line in run.stdout.splitlines():
        if line.startswith('RESULT\t'):
            _, suite, case, status, detail = (line.split('\t') + [''] * 5)[:5]
            results[(suite, case)] = (status, detail)
    return results


def common_sources():
    out = {}
    for name in ('UnityShims.cs', 'UnitCommon.cs'):
        with open(os.path.join(HERE, name), encoding='utf-8-sig') as f:
            out[name] = f.read()
    return out


def run_real(found, texts, tfm, work, verbose):
    sources = common_sources()
    for suite, text in texts.items():
        sources[suite + 'Tests.cs'] = assemble(suite, text, found)
    raw = build_and_run(os.path.join(work, 'real'), sources, tfm, verbose)
    results = []
    for suite in texts:
        if not any(s == suite for s, _ in raw):
            results.append(Result(LAYER, suite, 'FAIL', 'the suite ran no case'))
    for (suite, case), (status, detail) in raw.items():
        results.append(Result(LAYER, '%s: %s' % (suite, case), status, detail))
    return results


def run_mutants(found, texts, tfm, work, verbose):
    sources = common_sources()
    mutants = []   # (suite, tag, region, old, new)
    results = []
    for suite, text in texts.items():
        mutations = [m.groups() for m in MUTATE_REF.finditer(text)]
        if not mutations:
            results.append(Result(LAYER, 'self-test %s' % suite, 'FAIL', 'the suite has no @@MUTATE line'))
        for k, (region, old, new) in enumerate(mutations, 1):
            tag = 'm%d' % k
            label = 'self-test %s mutant %d [%s: "%s" -> "%s"]' % (suite, k, region, old, new)
            try:
                sources['%s_%s.cs' % (suite, tag)] = assemble(suite, text, found, (region, old, new), tag)
                mutants.append((suite, tag, label))
            except Broken as e:
                results.append(Result(LAYER, label, 'FAIL', str(e)))
    if not mutants:
        return results
    raw = build_and_run(os.path.join(work, 'mutants'), sources, tfm, verbose)
    for suite, tag, label in mutants:
        mine = [(case, status, detail) for (s, case), (status, detail) in raw.items() if s == '%s__%s' % (suite, tag)]
        failed = [(case, detail) for case, status, detail in mine if status == 'FAIL']
        if not mine:
            results.append(Result(LAYER, label, 'FAIL', 'the mutant ran no case'))
        elif failed:
            results.append(Result(LAYER, label, 'PASS', 'caught by %d of %d case(s), e.g. "%s": %s'
                                  % (len(failed), len(mine), failed[0][0], failed[0][1][:160])))
        else:
            results.append(Result(LAYER, label, 'FAIL', 'NOT caught: all %d case(s) still pass' % len(mine)))
    return results


def run_relief():
    if not os.path.isfile(RELIEF):
        return Result(LAYER, 'ReliefSimplifier (tools/check-relief-simplify.py)', 'SKIP', 'no ' + RELIEF)
    out = subprocess.run([sys.executable, RELIEF], capture_output=True, text=True, cwd=ROOT)
    last = [l for l in out.stdout.strip().splitlines() if l.strip()]
    return Result(LAYER, 'ReliefSimplifier (tools/check-relief-simplify.py, shipping + 3 must-fail variants)',
                  'PASS' if out.returncode == 0 else 'FAIL',
                  (last[-1] if last else out.stderr.strip()[-300:]))


def run(ctx=None):
    """Layer C. Returns a list of Result(layer, name, status, detail)."""
    self_test = bool(_opt(ctx, 'self_test', False))
    relief = bool(_opt(ctx, 'relief', True))
    verbose = bool(_opt(ctx, 'verbose', False))

    results = []
    work = tempfile.mkdtemp(prefix='questtree-unit-')
    try:
        found = regions()
        texts, problems = harnesses(found)
        results += [Result(LAYER, 'regions and harnesses', 'FAIL', p) for p in problems]
        if not found:
            results.append(Result(LAYER, 'regions and harnesses', 'FAIL', 'no BEGIN TESTABLE region in the source'))
        tfm = framework()
        results += run_real(found, texts, tfm, work, verbose)
        if self_test:
            results += run_mutants(found, texts, tfm, work, verbose)
    except Broken as e:
        results.append(Result(LAYER, 'unit harness', 'FAIL', str(e)))
    finally:
        shutil.rmtree(work, ignore_errors=True)

    if relief:
        results.append(run_relief())
    return results


def main(argv):
    ctx = {'self_test': '--self-test' in argv, 'relief': '--no-relief' not in argv, 'verbose': '-v' in argv}
    results = run(ctx)
    width = max(len(r.name) for r in results) if results else 0
    for r in results:
        print('%-4s  %-*s  %s' % (r.status, min(width, 90), r.name, r.detail))
    counts = collections.Counter(r.status for r in results)
    print('\nlayer C: %d PASS, %d FAIL, %d SKIP%s' % (counts['PASS'], counts['FAIL'], counts['SKIP'],
                                                    ' (self-test)' if ctx['self_test'] else ''))
    return 1 if counts['FAIL'] else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
