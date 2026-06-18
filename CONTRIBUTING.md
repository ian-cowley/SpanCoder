# Contributing to SpanCoder

We welcome contributions of all forms—bug reports, feature requests, documentation enhancements, and pull requests!

## How Can I Contribute?

### Reporting Bugs
* Check the existing issues first to avoid duplicates.
* Describe the bug clearly, including the environment (OS version, .NET version), the expected behavior, the actual behavior, and steps to reproduce.
* If applicable, provide logs or error traces.

### Suggesting Enhancements
* Open an issue explaining the proposed feature and why it would be beneficial to SpanCoder.
* Highlight any impact on performance, extensibility, or out-of-process isolation.

### Pull Requests
1. Fork the repository and create your branch from `main`.
2. Ensure the code compiles cleanly and adheres to the project's coding standards.
3. Keep changes focused. Avoid bundling unrelated fixes together.
4. Run all unit and headless UI tests before submitting:
   ```bash
   dotnet test
   ```
5. Include comments and update documentation if introducing new public APIs or extension boundaries.
6. Write descriptive commit messages.

## Coding Style & Architecture guidelines
* **Process Boundary Isolation**: Core editor Canvas (`SpanCoder.App`) and Core text buffer (`SpanCoder.Engine`) are decoupled. Keep extension and debugger hooks asynchronous and out-of-process.
* **Native AOT Trimming**: Ensure new C# implementations do not use runtime reflection. Rely on compile-time Source Generators (`[Command]`, `[MenuItem]`) for command routing.
* **Zero Allocation Path**: Keep the main text-rendering and line-slicing loops allocation-free using `ReadOnlySpan<char>` and pooled buffers where possible.
