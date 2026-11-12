# Contributing

Thanks for taking the time to improve idempotent-api.

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker (integration tests and benchmarks start Redis through Testcontainers; `docker compose up` runs the demo)

## Workflow

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Branch from `main` and keep each pull request to one concern.
3. Before pushing:

   ```bash
   dotnet build                       # must be warning-free: warnings are errors
   dotnet test                        # unit, property, golden and integration tests (Docker running)
   dotnet format --verify-no-changes
   ```

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) are required. They keep the history readable and make
the hand-written CHANGELOG easy to assemble at release time:

- `feat:` new capability (minor bump)
- `fix:` bug fix (patch bump)
- `feat!:` or a `BREAKING CHANGE:` footer (major bump)
- `docs:`, `test:`, `build:`, `ci:`, `refactor:`, `perf:`, `chore:` for everything else

## Changing store behaviour

The guarantee rests on `IIdempotencyStore` being atomic per key. A change to a store, or a new store:

1. must pass the shared contract in `tests/Shared/IdempotencyStoreContract.cs` (subclass it for the new store);
2. needs a multi-instance test in `tests/IdempotentApi.IntegrationTests` that counts real handler executions, not
   only the store's own state;
3. for the Lua scripts, keeps every read and write of one transition inside one script. Splitting a script into
   several commands reintroduces the race measured in `docs/benchmarks.md`.

## Changing the HTTP contract

Status codes, problem types and replayed headers are a contract with API clients. Changes to them:

- update `docs/idempotency-specification.md`;
- regenerate the golden files with `UPDATE_GOLDEN=1 dotnet test --project tests/IdempotentApi.UnitTests` and review
  the diff like code;
- cite the draft section they follow (or deliberately deviate from) in an ADR.

## Changing the token bucket

`TokenBucketMath` (C#) and `Scripts/token_bucket.lua` implement the same arithmetic. Change both, keep the FsCheck
properties in `TokenBucketPropertyTests` green, and keep the Lua/C# parity test in `RedisTokenBucketTests` passing.

## Architecture decisions

A change to a decision recorded in `docs/adr/` needs a new ADR that supersedes the old one; do not edit the old ADR in
place. Use the existing files as the format (MADR: context, options considered, decision, consequences).

## Benchmarks

Numbers in the README and `docs/benchmarks.md` are measured, never estimated. If a change affects performance, rerun
the relevant scenario (`dotnet run -c Release --project benchmarks/IdempotentApi.Benchmarks -- <scenario>`), commit the
JSON from `benchmark-results/`, and update the docs with the environment and host load.
