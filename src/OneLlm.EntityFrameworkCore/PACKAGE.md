# RecursiveAI.OneLlm.EntityFrameworkCore

**LLM inference and agent runtime for .NET**: an instance store and a record sink for OneLLM, as entity configurations on your own Entity Framework Core `DbContext`. OneLLM is unaffiliated with the `onellm` package on PyPI and with the OneLLM research model.

```csharp
public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder b) => b.ApplyOneLlm();
}

var store = new EfInstanceStore(() => factory.CreateDbContext());
var sink  = new EfRecordSink(() => factory.CreateDbContext());
```

Instances are kept as their readable YAML beside header columns, so they can be queried; one runner holds an instance at a time through an expiring lease row. See [stores](https://github.com/recursive-ai-oss/onellm-csharp/blob/main/docs/stores.md). Tested on SQLite; other providers use the same relational mapping.

Requires `RecursiveAI.OneLlm` and Entity Framework Core. Source and issues: https://github.com/recursive-ai-oss/onellm-csharp

Licence: `MIT OR Apache-2.0`. Copyright (c) Recursive AI, LLC.
