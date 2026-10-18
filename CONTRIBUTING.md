# Contributing

Vigil is a BOF Studios project.

## Development setup

You need the .NET 8 SDK on Windows 10 (2004) or later.

```bash
git clone https://github.com/BofStudios/vigil.git
cd vigil
dotnet build Vigil.sln
dotnet test Vigil.sln
```

Both must be clean before you open a pull request. Live tests against a real model run only
with `VIGIL_LIVE=1` set, because they spend the key's quota.

To look at a change to the window without disturbing whatever is on screen:

```bash
src/Vigil/bin/Debug/net8.0-windows10.0.19041.0/win-x64/Vigil.exe --preview out
```

## Code style

- Code and comments in English. Comments say why, not what.
- Avoid adding dependencies. The app uses NAudio and System.Speech and nothing else.
- Every new rule or tool needs a test.

## Adding a tool

1. Write the handler in `src/Vigil/Core/Tools/` and add a `ToolSpec` to that file's `All`.
2. Pick the right `Risk`:
   - `Safe` only for read-only, trivially reversible work
   - `Moderate` for anything that changes the system
   - `High` for irreversible work (deleting, ending processes, installing)
3. Call the guard inside the handler (`ctx.Guard.CheckAction`, `CheckPath`, `CheckCommand`) before
   doing anything - the guard, not the spec, is what actually decides.
4. Anything that takes the mouse, keyboard or screen goes through `Rules.AlwaysAsk`.
5. Expose it to the model in `Compact.cs` - add it to a group if one fits; every request carries
   every schema, so keep the description short.

## Security rules

Changes to `Rules.cs` need a test in `SecurityTests.cs` showing the command is caught - and that
an everyday command is not.
