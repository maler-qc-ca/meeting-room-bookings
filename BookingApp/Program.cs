using BookingApp;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<BookingDatabase>();

var app = builder.Build();
await app.Services.GetRequiredService<BookingDatabase>().InitializeAsync();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapBookingEndpoints();

app.Run();

public partial class Program { }
