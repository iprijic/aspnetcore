using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Microsoft.AspNetCore.OpenApi
// builder.Services.AddOpenApi(); // AspNetCore
builder.Services.AddEndpointsApiExplorer(); // AspNetCore
builder.Services.AddSwaggerGen(c => {
    c.UseOneOfForPolymorphism();
}); // Swashbuckle
builder.Services.AddOpenApiDocument(); // NSwag

// builder.Services.AddSingleton<ISayHello, EnglishHello>();
// builder.Services.AddSingleton<TodoDbContext>();

var app = builder.Build();

app.MapOpenApiDocument(); // AspNetCore
app.UseSwagger(); // Swashbuckle
app.UseSwaggerUI(); // Swashbuckle
app.UseOpenApi(options => {
    options.Path = "/nswag/{documentName}/openapi.json";
}); // NSwag

app.MapPost("/test", (Shape shape) => { });

app.Run();

public interface ITodo
{
    int Id { get; }
}

public enum TodoStatus
{
    NotStarted,
    InProgress,
    Completed
}

public record TodoFromInterface(int Id, string Title, bool Completed, DateTime CreatedAt) : ITodo;

public record Todo(int Id, string Title, bool Completed, DateTime CreatedAt);
public record TodoWithDueDate(int Id, string Title, bool Completed, DateTime CreatedAt, DateTime DueDate) : Todo(Id, Title, Completed, CreatedAt);

[JsonDerivedType(typeof(Triangle), typeDiscriminator: "triangle")]
[JsonDerivedType(typeof(Square), typeDiscriminator: "square")]
public class Shape
{
    public string Color { get; set; } = "blue";
    public int Sides { get; set; }
}

public class Triangle : Shape { }
public class Square : Shape { }
