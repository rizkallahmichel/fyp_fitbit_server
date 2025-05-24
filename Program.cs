using Google.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;

namespace FitServer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddControllers();
            // Add Swagger
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo
                {
                    Title = "Fitbit API",
                    Version = "v1"
                });
            });
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddHostedService<FitbitDataLoader>();
            builder.Services.AddSingleton<FirebaseService>();
            builder.Services.AddDistributedMemoryCache();
            builder.Services.AddSession(options =>
            {
                options.IdleTimeout = TimeSpan.FromMinutes(30);
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
            });
            // Add HttpClientFactory
            builder.Services.AddHttpClient();

            // Add Session support
            builder.Services.AddDistributedMemoryCache();
            builder.Services.AddSession(options =>
            {
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
            });

            var app = builder.Build();

            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Fitbit API V1");
                c.RoutePrefix = string.Empty;
            });


            app.UseSession();
            app.UseHttpsRedirection();

            app.UseSession();

            app.UseAuthorization();

            app.UseMiddleware<FitbitAuthMiddleware>();

            app.MapControllers();

            app.Run();
        }
    }
}
