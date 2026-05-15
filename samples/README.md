# ChokaQ Samples

The repository contains two kinds of samples.

## Source Samples

These samples are part of the main repository solution and use
`ProjectReference` entries into `src`.

| Sample | Purpose | Open with |
|---|---|---|
| `ChokaQ.Sample.Bus` | Full Bus-style web sample with SQL Server storage and The Deck. | `ChokaQ.sln` |
| `ChokaQ.Sample.Pipe` | Pipe-style sample for direct pipeline handling. | `ChokaQ.sln` |

Use these when changing ChokaQ itself. They are convenient for local debugging
because stepping into product code works directly from the same solution.

## Package Consumer Sample

`ChokaQ.Sample.NuGetLab` is intentionally separate from the main solution. It
references the top-level `ChokaQ` NuGet package, not local product projects.

| Sample | Purpose | Open with |
|---|---|---|
| `ChokaQ.Sample.NuGetLab` | Release smoke app that proves a normal host can consume ChokaQ from a feed. | `samples/ChokaQ.Sample.NuGetLab/ChokaQ.Sample.NuGetLab.sln` |

Keep this sample out of the root `ChokaQ.sln`. The root solution must be able to
build from source without requiring prepacked `.nupkg` files, while NuGetLab
must fail loudly if the package feed is broken.
