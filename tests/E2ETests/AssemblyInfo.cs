using Xunit;

// Disable parallel test execution within the E2E test assembly to ensure database and WebApplicationFactory isolation
[assembly: CollectionBehavior(DisableTestParallelization = true)]
