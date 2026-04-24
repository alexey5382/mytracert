using Microsoft.AspNetCore.StaticFiles;

namespace Lab5
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.UseUrls("http://localhost:5000");
            builder.Services.AddSingleton<StorageService>();

            var app = builder.Build();

            app.MapPut("{*filePath}", async (string? filePath, HttpRequest request, StorageService storage) =>
            {
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    return Results.BadRequest("Имя файла не может быть пустым.");
                }

                try
                {
                    bool isNew = await storage.SaveFileAsync(filePath, request.Body);
                    return isNew ? Results.Created($"/{filePath}", null) : Results.NoContent();
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.StatusCode(403);
                }
                catch (Exception ex)
                {
                    return Results.Problem(ex.Message);
                }
            });

            var provider = new FileExtensionContentTypeProvider();

            app.MapGet("{*filePath}", IResult (string? filePath, StorageService storage, HttpContext context) =>
            {
                try
                {
                    string fullPath = storage.GetSafePath(filePath);

                    if (File.Exists(fullPath))
                    {
                        if (!provider.TryGetContentType(fullPath, out var contentType))
                        {
                            contentType = "application/octet-stream";
                        }

                        string fileName = Path.GetFileName(fullPath);
                        context.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{fileName}\"");

                        context.Response.Headers.Append("Cache-Control", "no-store, no-cache, must-revalidate");
                        context.Response.Headers.Append("Pragma", "no-cache");
                        context.Response.Headers.Append("Expires", "0");

                        return Results.File(fullPath, contentType);
                    }

                    if (Directory.Exists(fullPath))
                    {
                        var content = storage.GetDirectoryContent(filePath);

                        return Results.Ok(content);
                    }

                    return Results.NotFound("Файл или каталог не найден.");
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.StatusCode(403);
                }
                catch (Exception ex)
                {
                    return Results.Problem(ex.Message);
                }
            });

            app.MapMethods("{*filePath}", new[] { "HEAD" }, (string? filePath, StorageService storage, HttpContext context) =>
            {
                try
                {
                    string fullPath = storage.GetSafePath(filePath);

                    if (File.Exists(fullPath))
                    {
                        var fileInfo = new FileInfo(fullPath);

                        context.Response.Headers.ContentLength = fileInfo.Length;

                        context.Response.Headers.LastModified = fileInfo.LastWriteTimeUtc.ToString("R");

                        return Results.Ok();
                    }

                    if (Directory.Exists(fullPath))
                    {
                        return Results.Ok();
                    }

                    return Results.NotFound();
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.StatusCode(403);
                }
                catch (Exception ex)
                {
                    return Results.Problem(ex.Message);
                }
            });

            app.MapDelete("{*filePath}", (string? filePath, StorageService storage) =>
            {
                try
                {
                    string fullPath = storage.GetSafePath(filePath);

                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                        return Results.NoContent();
                    }

                    if (Directory.Exists(fullPath))
                    {
                        Directory.Delete(fullPath, true);
                        return Results.NoContent();
                    }

                    return Results.NotFound("Файл или каталог не найден.");
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.StatusCode(403);
                }
                catch (Exception ex)
                {
                    return Results.Problem(ex.Message);
                }
            });

            app.Run();
        }
    }

}
