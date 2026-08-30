# Contributing

Vigil is a BOF Studios project. Contributions are welcome.

## Development setup

```bash
git clone https://github.com/BofStudios/vigil.git
cd vigil
python -m venv .venv
.venv\Scripts\activate        # Windows
source .venv/bin/activate     # macOS / Linux
pip install -e ".[all,dev]"
```

Before you push:

```bash
ruff check .
pytest -q
```

Both must be clean before you open a pull request.

## Code style

- Line length 110, `ruff` rules apply.
- Code and comments in English.
- Avoid adding dependencies. If one is unavoidable, put it under
  `[project.optional-dependencies]` and make the module importable without it
  (the `AVAILABLE = False` pattern).
- Every new tool needs at least one test.

## Adding a tool

1. Write your module under `vigil/tools/` and publish a `TOOLS` list.
2. Pick the right `Risk` level:
   - `SAFE` only for read-only, trivially reversible work
   - `MODERATE` for anything that changes the system
   - `HIGH` for irreversible work (deleting, terminating, installing)
3. Call `ctx.guard.check_action(...)` for destructive work and honour the answer.
4. Register it with `registry.load_module(".your_module")` in `build_registry`.
5. Add a test in `tests/test_tools.py` and update the README table.

## Security rules

Pull requests that **weaken** `BLOCKED_RULES` in `vigil/security.py` will not be accepted.
Proposals for new blocks — especially bypasses you have found — are the most valuable
contribution you can make.

If a rule produces a false positive (blocks a harmless command), open an issue with the full
command and we will narrow the rule.

## Commits and pull requests

- Keep commit messages short and descriptive: `tools: add window focus`
- Describe what changed and how you tested it.
- Screenshots or a GIF are a big plus.

## Conduct

Be respectful, be helpful, and never talk down to beginners. That is the whole code.
