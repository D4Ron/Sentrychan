# Suwayomi-Server responses

Test fixtures for the Mihon extension bridge's GraphQL client.

**Recorded** from a real Suwayomi-Server v2.3.2243 (the pinned release) on Linux, using only its
built-in *Local source* over two invented titles on disk — no extension, repository or site was
involved: `about`, `sources-local-only`, `filters-local`, `popular`, `latest`, `search`, `manga`, `chapters`,
`pages`, `bycond`, `error-null`.

**Written by hand** from that release's schema, for shapes the Local source can't produce (it has
one sort filter and no settings): `sources`, `filters-all`, `preferences`, `extensions`, `repos`.
Hosts are `example.test`; package names are `test.example.*`.
