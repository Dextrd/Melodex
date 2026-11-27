# Contributing to Melodex

Thanks for helping. Bug reports, fixes, docs, plugins and ideas are all welcome. This page says how to get a change
from an idea to a merged pull request.

## Ways to help

- **Report a bug**: open an issue with the bug report form. Say what you did, what you expected and what happened,
  with your Melodex version and anything from the log (**Settings → Log**) that looks related.
- **Suggest a feature**: open an issue with the feature request form, starting from the problem it solves.
- **Fix something**: issues labelled [good first issue](https://github.com/Dextrd/Melodex/labels/good%20first%20issue)
  are a good place to start, and [help wanted](https://github.com/Dextrd/Melodex/labels/help%20wanted) ones are
  waiting for someone to pick them up.
- **Improve the docs**: the user guide is in [docs/guide](docs/guide/README.md) and is also shown in the web app, so a
  clearer sentence there helps everyone.
- **Write a plugin**: the [plugin contracts](docs/contracts/README.md) say how. Retrieval plugins live in repositories
  of their own (`dextrd/melodex-retriever-<name>`); changes to one go to its repository, not this one.

Found a security problem? Please don't open an issue: see [SECURITY.md](SECURITY.md).

## Before you start

- **Bug fixes, docs and small changes** can go straight to a pull request.
- **New features, and anything big**, need an issue first, agreed before you start work. Say what you'd like to
  build and how, and wait for a yes. It saves you building something that can't be merged, because it clashes with
  where Melodex is going or with work already under way.

If you're picking up an existing issue, comment on it first, so two people don't do the same work.

## Branches

- **`dev`** is where work happens. Every pull request goes there, and it's what the next release will be.
- **`main`** is the latest release, and changes only when there's a new one. The compose file, the guide's links
  and the images' `latest` tag all follow it, so what's on `main` is what people are running.

At a release, `main` is brought up to `dev` and tagged.

## Setting up

You'll need the .NET 10 SDK, Python 3 with `mutagen`, ffmpeg and Docker. [Developing Melodex](docs/development.md)
covers the layout and how to build and test it, and [Testing without a real library](docs/testing.md) sets up the
sandbox: a throwaway stack over a generated test library.

```bash
python3 tools/testlib/generate.py   # once: the gate test needs the test library
dotnet test
sandbox/sandbox.sh up               # to try changes in the web app
```

**Try changes that write files in the sandbox, never on your own music.** Formatting, Even volume, upgrades and
imports rewrite or replace files, and a bug in a change to them can damage a library.

## Making a change

- **Branch from `dev`**, and open the pull request against `dev`, one issue per pull request. Keep it to what the issue needs: a tidy-up somewhere else is
  its own pull request.
- **Match the code around it**: its naming, its comments and how it's laid out. The web app talks to the core only
  through the public API, at `/api/v1`; if it needs something new, add it to the API and to `Melodex.Client`.
- **Add tests** for what you change, in `tests/Melodex.Core.Tests`.
- **Schema changes need a migration**: see [Developing Melodex](docs/development.md). CI checks the model matches
  the migrations.
- **Update the docs** when something a user sees changes: the [guide](docs/guide/README.md) for people using
  Melodex, the [client guide](docs/client-guide.md) for the API, and the [contracts](docs/contracts/README.md) for
  plugins.
- **Add a changelog entry** under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md), in the *Added*, *Changed* or
  *Fixed* section, written for the person using Melodex, with the issue's number at the end:

  ```markdown
  - **Sign out from your account**: a Sign out button at the foot of **Settings → Your account** (#123).
  ```

- **New plugins are named on the About page**, under "Built on". A test checks every plugin is.
- **Write plainly**, in the web app and the docs alike: short sentences, the words people use, and British spelling.

## Tests and CI

Run the tests near your change while you work, for example `dotnet test --filter AboutPageTests`, and the whole suite
before you open the pull request. CI runs on every pull request, and on every push to `dev` and `main`. It builds Melodex, checks the web app's scripts
parse, runs every test, checks the migrations, and builds and starts the Docker image. A pull request is merged only
once CI passes.

A few tests depend on timing and can fail on a busy CI runner. If one fails in a part of Melodex you didn't touch, say
so in the pull request and it'll be run again.

## Pull requests

- Fill in the template, and link the issue with `Closes #123`.
- Give it a title that says what changes for the user, such as "Sign out from your account". It becomes the commit's
  subject when it's merged.
- Keep it as a draft until it's ready for review.
- Changes may be asked for in review; push them to the same branch.
- Pull requests are squash-merged by a maintainer, so the history on your branch doesn't need to be tidy.

## Using AI tools

You're welcome to use AI tools to write code, tests or docs. But you're the author: you must understand every line,
have tested it, and be able to explain it in review. If a tool wrote much of a pull request, say so in its description.

## Licence

Melodex is [MIT licensed](LICENSE). By contributing, you agree your contribution is under the same licence. There's
nothing to sign.

## Questions

Open an issue and label it [question](https://github.com/Dextrd/Melodex/labels/question), or ask on the issue you're
working on.
