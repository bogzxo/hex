using System.Text.RegularExpressions;

namespace Hex.Core;

internal static class PythonPytest
{
    // Run this in the selected interpreter, off the UI thread. Parse test syntax without
    // importing user code; the selected framework remains responsible for collection.
    internal const string Detect = """
import ast, os, sys, tokenize
def uses_pytest():
    ignored = {'venv', 'env', 'node_modules', 'site-packages', '__pycache__', 'build', 'dist'}
    for directory, folders, files in os.walk(os.getcwd()):
        folders[:] = [name for name in folders if not name.startswith('.') and name not in ignored
                      and not os.path.isfile(os.path.join(directory, name, 'pyvenv.cfg'))]
        if 'conftest.py' in files: return True
        for name in files:
            if not (name.startswith('test_') and name.endswith('.py') or name.endswith('_test.py')): continue
            try:
                with tokenize.open(os.path.join(directory, name)) as stream:
                    module = ast.parse(stream.read())
            except (OSError, SyntaxError, UnicodeError): continue
            for node in module.body:
                if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name.startswith('test'): return True
                if isinstance(node, ast.ClassDef) and node.name.startswith('Test') and not node.bases:
                    if any(isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef)) and item.name.startswith('test') for item in node.body): return True
            for node in ast.walk(module):
                if isinstance(node, ast.Import) and any(item.name == 'pytest' for item in node.names): return True
                if isinstance(node, ast.ImportFrom) and node.module == 'pytest': return True
    return False
use_pytest = uses_pytest()
if use_pytest: sys.argv[3] = os.getcwd()
""";

    internal static bool IsProject(string root)
    {
        if (new[] { "pytest.ini", ".pytest.ini", "pytest.toml", ".pytest.toml", "conftest.py" }.Any(name => File.Exists(Path.Combine(root, name)))) return true;
        foreach (var (name, section) in new[] { ("pyproject.toml", @"tool\.pytest(?:\.ini_options)?"), ("tox.ini", "pytest"), ("setup.cfg", "tool:pytest") })
        {
            string file = Path.Combine(root, name);
            if (File.Exists(file) && Regex.IsMatch(File.ReadAllText(file), @"(?m)^\s*\[" + section + @"\]\s*(?:#.*)?$")) return true;
        }
        return false;
    }

    internal const string Runner = """
import json, os, sys
import pytest
destination, mode, root, *selected = sys.argv[1:]
sys.path.insert(0, root)
rows, results, errors, active = [], [], [], {}
def save():
    temporary = destination + '.tmp'
    with open(temporary, 'w', encoding='utf-8') as stream:
        json.dump({'framework': 'pytest', 'tests': rows, 'results': results, 'errors': errors}, stream)
    os.replace(temporary, destination)
class Reporter:
    def pytest_collection_finish(self, session):
        for item in session.items:
            rows.append({'id': item.nodeid, 'class': item.parent.nodeid, 'name': item.name,
                         'file': str(item.path), 'line': item.location[1] or 0})
        save()
    def pytest_collectreport(self, report):
        if report.failed: errors.append(report.longreprtext)
    def pytest_runtest_logreport(self, report):
        row = active.get(report.nodeid)
        if row is None:
            description = next((item for item in rows if item['id'] == report.nodeid), {'id': report.nodeid})
            row = dict(description, outcome='passed', message='', stack='', duration=0)
            active[report.nodeid] = row
        row['duration'] += report.duration
        row['output'] = report.capstdout + report.capstderr
        if report.failed:
            row.update(outcome='failed', message=str(report.longrepr), stack=report.longreprtext)
            crash = getattr(report.longrepr, 'reprcrash', None)
            if crash:
                row.update(message=crash.message, failureFile=os.path.abspath(crash.path), failureLine=crash.lineno-1)
        elif report.skipped and row['outcome'] != 'failed':
            row.update(outcome='skipped', message=str(report.longrepr))
        if report.when == 'teardown':
            results.append(active.pop(report.nodeid))
            save()
    def pytest_sessionfinish(self, session, exitstatus):
        save()
save()
args = ['--collect-only', '-q'] if mode == 'discover' else ['-q']
if mode == 'debug': args += ['-s']
args += selected
status = pytest.main(args, plugins=[Reporter()])
save()
sys.exit(int(status) if int(status) in (3, 4) else 0)
""";
}
