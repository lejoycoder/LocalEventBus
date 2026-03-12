# Repository Guidelines

## Project Structure & Module Organization
`src/LocalEventBus/` contains the .NET 8 library and solution file. Keep public contracts in `Abstractions/`, DI wiring in `DependencyInjection/`, matcher implementations in `Matchers/`, and runtime internals in `Internal/`.  
`tests/LocalEventBus.Tests/` holds the xUnit suite; shared test event types live in `Events/`.  
`benchmarks/LocalEventBus.Benchmarks/` contains BenchmarkDotNet scenarios for throughput and dispatch changes.  
`doc/` stores supporting documents and NuGet packaging assets such as `doc/nuget/local-eventbus-icon.jpeg`.

## Build, Test, and Development Commands
- `dotnet build src/LocalEventBus/LocalEventBus.sln` builds the library, tests, and benchmarks together.
- `dotnet test tests/LocalEventBus.Tests/LocalEventBus.Tests.csproj -p:RestoreSources=https://api.nuget.org/v3/index.json` runs the xUnit suite and avoids broken machine-level NuGet sources.
- `dotnet run --project benchmarks/LocalEventBus.Benchmarks/LocalEventBus.Benchmarks.csproj -c Release` executes BenchmarkDotNet benchmarks.
- `dotnet pack src/LocalEventBus/LocalEventBus.csproj -c Release` creates the NuGet package using the root `README.md` and `doc/nuget` assets.

## Coding Style & Naming Conventions
Use 4-space indentation and file-scoped namespaces. Follow existing C# conventions: `PascalCase` for public types and members, `_camelCase` for private fields, and an `Async` suffix on asynchronous methods. Keep nullable reference annotations enabled, prefer `record` for simple event payloads, and add XML documentation to public API surface changes. There is no committed formatter or analyzer config, so match the existing style and use `dotnet format` locally if needed.

## Testing Guidelines
Tests use xUnit. Name test files `*Tests.cs` and use method names in the `Method_Should_Result` style, for example `PublishAsync_Should_Invoke_Typed_Subscriber`. Add coverage for new routing rules, retry behavior, disposal safety, and thread-related changes. When behavior changes affect performance claims, update or add a benchmark alongside the tests.

## Commit & Pull Request Guidelines
Recent history favors short, imperative commit subjects, usually in Chinese, such as `优化publish接口` or `增加订阅者线程调用选项`; `chore:` is used for release and metadata updates. Keep commits focused and describe one logical change each. PRs should summarize behavior changes, list the commands you ran, link related issues, and call out README or package metadata updates when public API or NuGet output changes. Include benchmark results for performance-sensitive work.
