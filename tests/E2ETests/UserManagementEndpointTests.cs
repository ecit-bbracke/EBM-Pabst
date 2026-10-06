using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using DocumentRagSystem.WebApi.DTOs;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DocumentRagSystem.E2ETests;

public class UserManagementEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public UserManagementEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@ebmpabst.dk", "Admin123!"));
        loginResponse.EnsureSuccessStatusCode();
        return client;
    }

    private async Task<HttpClient> CreateNonAdminClientAsync()
    {
        var adminClient = await CreateAuthenticatedClientAsync();
        var normalEmail = $"employee_{Guid.NewGuid():N}@ebmpabst.dk";
        var createResp = await adminClient.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(normalEmail, "Password123!"));
        createResp.EnsureSuccessStatusCode();

        var nonAdminClient = _factory.CreateClient();
        var loginResponse = await nonAdminClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(normalEmail, "Password123!"));
        loginResponse.EnsureSuccessStatusCode();
        return nonAdminClient;
    }

    [Fact]
    public async Task GetAdminUsers_WhenUnauthenticated_ReturnsUnauthorized()
    {
        var unauthenticatedClient = _factory.CreateClient();
        var response = await unauthenticatedClient.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAdminUsers_WhenNonAdminUser_ReturnsForbidden()
    {
        var nonAdminClient = await CreateNonAdminClientAsync();
        var response = await nonAdminClient.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UsersPage_WhenNonAdminUser_RedirectsToIndex()
    {
        var nonAdminClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var normalEmail = $"employee_{Guid.NewGuid():N}@ebmpabst.dk";
        var adminClient = await CreateAuthenticatedClientAsync();
        await adminClient.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(normalEmail, "Password123!"));
        await nonAdminClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(normalEmail, "Password123!"));

        var response = await nonAdminClient.GetAsync("/users.html");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/index.html", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task GetAdminUsers_WhenAuthenticatedAdmin_ReturnsUsersList()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/admin/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var users = await response.Content.ReadFromJsonAsync<List<UserSummaryDto>>();

        Assert.NotNull(users);
        Assert.NotEmpty(users);
        Assert.Contains(users, u => u.Email.Equals("admin@ebmpabst.dk", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateAdminUser_CreatesNewUserSuccessfully()
    {
        var client = await CreateAuthenticatedClientAsync();
        var newEmail = $"user_{Guid.NewGuid():N}@ebmpabst.dk";
        var createRequest = new CreateUserRequest(newEmail, "Password123!");

        var response = await client.PostAsJsonAsync("/api/admin/users", createRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<UserSummaryDto>();
        Assert.NotNull(created);
        Assert.Equal(newEmail, created.Email);

        // Verify the user can log in with new credentials
        var userClient = _factory.CreateClient();
        var loginResp = await userClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(newEmail, "Password123!"));
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
    }

    [Fact]
    public async Task DeleteAdminUser_WhenAttemptingToDeleteDefaultAdmin_ReturnsBadRequest()
    {
        var client = await CreateAuthenticatedClientAsync();
        var usersResponse = await client.GetAsync("/api/admin/users");
        var users = await usersResponse.Content.ReadFromJsonAsync<List<UserSummaryDto>>();
        var defaultAdmin = users!.Find(u => u.Email.Equals("admin@ebmpabst.dk", StringComparison.OrdinalIgnoreCase));

        var response = await client.DeleteAsync($"/api/admin/users/{defaultAdmin!.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
