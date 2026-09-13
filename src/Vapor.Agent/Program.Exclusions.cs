using System.Diagnostics.CodeAnalysis;

// Merges with the compiler-generated top-level Program (global namespace).
// The startup assembly is exercised end-to-end by Vapor.E2E.Tests across real
// CP/Agent processes, which coverage instrumentation cannot reach; excluded
// from the unit-test coverage scope (see tests/TESTING.md).
[ExcludeFromCodeCoverage]
internal partial class Program;
