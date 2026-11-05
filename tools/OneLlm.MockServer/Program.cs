using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

var app = WebApplication.CreateBuilder(args).Build();
app.MapGet("/", () => "onellm mock server");
app.Run();
