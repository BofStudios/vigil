# Publishing Guide

A step-by-step checklist for putting Vigil on GitHub and PyPI.

---

## 1. GitHub repository

### Repository details

| Field | Value |
|---|---|
| **Name** | `vigil` |
| **Description** | `A free AI agent that operates your computer from the terminal. Never disables your security protections.` |
| **Website** | the BOF Studios site, if you have one |
| **Topics** | `ai-agent` `cli` `python` `automation` `groq` `ollama` `llm` `computer-use` `terminal` `assistant` |

### First push

```bash
git branch -M main
git remote add origin https://github.com/BofStudios/vigil.git
git push -u origin main
```

> If the repo lives under a different account, update the `github.com/BofStudios/vigil` links in
> `README.md` and `pyproject.toml`.

### Repository settings

- Keep **Issues** on (bug reports and new block proposals)
- **Discussions** is worth enabling for usage questions
- **Actions** works out of the box via `.github/workflows/ci.yml`
- Branch protection on `main`: require the `test` status check

---

## 2. Release tag

```bash
git tag -a v0.1.0 -m "Vigil v0.1.0"
git push origin v0.1.0
```

On GitHub: **Releases → Draft a new release** → tag `v0.1.0`, title `Vigil v0.1.0`.

Release note draft:

```markdown
First release.

**What's in it**
- 41 tools across 6 groups: terminal, file, system, screen, browser, memory
- Groq (free cloud) and Ollama (fully local) providers
- Four-level security layer: safe / moderate / high / blocked
- Blocked actions never run, not even in --yolo mode
- Persistent memory (global + project) and a plugin system
- An audit log for every decision

**Install**
pip install vigil-cli
vigil setup
```

---

## 3. Publishing to PyPI

The name `vigil` is taken on PyPI, so the package is **`vigil-cli`** while the command stays `vigil`.

```bash
pip install build twine
python -m build                    # produces .whl and .tar.gz in dist/
twine check dist/*
```

Try TestPyPI first:

```bash
twine upload --repository testpypi dist/*
pip install --index-url https://test.pypi.org/simple/ vigil-cli
```

If that looks right, publish for real:

```bash
twine upload dist/*
```

> Put your API token in `~/.pypirc` or the `TWINE_PASSWORD` environment variable.
> Never commit it.

Verify afterwards:

```bash
pip install vigil-cli
vigil doctor
```

---

## 4. Demo GIF

A GIF at the top of the README moves the needle more than anything else. Record with
[asciinema](https://asciinema.org) + [agg](https://github.com/asciinema/agg), or ScreenToGif on
Windows. Use Windows Terminal, dark theme, roughly 100x30.

**Script (~40 seconds):**

1. `vigil` → the banner appears
2. `how many files are on my desktop, and what are the three biggest?`
   → `list_dir` + `run_command` run, answer appears
3. `create report.md here and write today's summary into it`
   → the approval panel appears, confirmed with **y**
4. `disable windows defender`
   → the red **blocked** panel appears ← *this is the money shot*
5. `/audit` → the decision list

Save it as `docs/demo.gif` and put it at the top of the README:

```markdown
<img src="docs/demo.gif" alt="Vigil demo" width="700">
```

---

## 5. Pre-release checklist

- [ ] `ruff check .` is clean
- [ ] `pytest -q` fully passes
- [ ] `vigil doctor` looks healthy
- [ ] The version in `pyproject.toml` matches `__version__` in `vigil/__init__.py`
- [ ] The model table in the README matches `vigil models`
- [ ] No `.env`, `~/.vigil/config.json` or API key anywhere in the repo
- [ ] LICENSE and SECURITY.md are in place
- [ ] CI is green

Key-leak check:

```bash
git grep -iE "gsk_[a-z0-9]{20,}|sk-[a-z0-9]{20,}"
```

---

## 6. Announcing

- **Instagram (@bofstudios):** demo GIF + "we built an open-source AI agent that runs your
  computer from the terminal" + repo link
- **Reddit:** r/LocalLLaMA (the Ollama support lands well there), r/Python, r/commandline
- **Hacker News:** a Show HN post — lead with the security model, that is the differentiator
- **Product Hunt:** worth it once the demo GIF exists

When you pitch it, lead with: *"it runs your computer but never touches your security
protections — not even in `--yolo` mode"*. Most comparable projects do not draw that line.

---

## 7. After a release

To ship a new version:

1. Bump `__version__` in `vigil/__init__.py`
2. Bump `version` in `pyproject.toml`
3. Add a CHANGELOG entry
4. `git tag -a vX.Y.Z` → push
5. `python -m build && twine upload dist/*`
