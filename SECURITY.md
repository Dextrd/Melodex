# Security policy

## Supported versions

Security fixes go into the latest release. If you're running an older one, upgrade first: see
[upgrading](docs/guide/upgrading.md).

## Reporting a vulnerability

Please don't report security problems in public issues, pull requests or discussions.

Report them privately instead, through GitHub: on the repository's
[Security tab](https://github.com/Dextrd/Melodex/security), choose **Report a vulnerability**. Only the maintainers
can see the report.

Say:

- what the problem is, and what someone could do with it;
- how to reproduce it, with the Melodex version and any settings it needs;
- whether it needs an account, and with which permissions, or whether anyone who can reach the server can use it.

You'll hear back within a week. Once it's fixed and released, the advisory is published, crediting you unless you'd
rather not be named.

## What's in scope

Melodex itself: the core, the web app, the beets worker and the built-in plugins in this repository. Retrieval
plugins have repositories of their own; report problems with one there, the same way.

Melodex is meant to run on your own network or behind a reverse proxy with HTTPS. Problems that need someone to
already be the admin, or to control the server's files, aren't vulnerabilities in Melodex.
