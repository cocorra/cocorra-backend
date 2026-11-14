# Database migrations

## The rule

**Every migration must be backward-compatible with the previous release.**

The release that adds a migration must leave a schema that the *previous* release's code can
still run against. Breaking changes are split across releases: **expand first, contract in a
later release.**

## Why

- The API applies pending migrations itself at startup (`Database.MigrateAsync()` in
  `Program.cs`), before it reports healthy.
- If the new release then fails its health check, `deploy/deploy.sh` rolls back to the previous
  image automatically. **It does not roll the database back.** The previous code runs against
  the new schema.
- The database is not restored automatically, because a restore would also discard every write
  made between the pre-deploy backup and the rollback. deploy.sh prints the backup it took and
  the manual restore commands. Restoring is a deliberate, manual decision.

So a migration that the previous release cannot tolerate turns a routine rollback into an
outage.

## Expand / contract

| Change | Release N (expand) | Release N+1 or later (contract) |
|---|---|---|
| Add a column | Add it **nullable** or **with a default**. Old code ignores it. | Make it required only once every running release writes it. |
| Remove a column | Stop reading and writing it in code. Leave the column. | Drop the column. |
| Rename a column | Add the new column; write both; backfill; read the new one. | Drop the old column. |
| Change a column's type | Add a new column of the new type; write both; backfill. | Switch reads, then drop the old column. |
| Add a table | Fine in one release. | — |
| Remove a table | Stop using it in code. | Drop the table. |
| Add a NOT NULL constraint / unique index / FK | Fix the data and make code comply first. | Add the constraint. |
| Tighten a length (e.g. `nvarchar(200)` → `(100)`) | Make code respect the new limit. | Shrink the column. |

**Never, in the same release that stops using it:** drop or rename a column or table, or change
a column's type in place.

## Checklist for a PR that adds a migration

- [ ] Could the **previous** release's code run correctly against the schema after this
      migration? (New columns nullable or defaulted, nothing it reads removed or renamed.)
- [ ] Any drop or rename removes something that the **previous** release already stopped using.
- [ ] Data backfills are idempotent and batched for large tables (they run during startup,
      inside the health-check window of `deploy/docker-compose.prod.yml`'s `start_period`).
- [ ] Generated with `dotnet ef migrations add <Name> --project Cocorra.DAL --startup-project Cocorra.API`.
      The local connection string comes from `appsettings.Development.json` (see
      `AppDbContextFactory`).
- [ ] Reviewed the generated `Up()`: EF expresses some renames as drop + add, which loses data.
