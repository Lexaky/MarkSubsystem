using MarkSubsystem.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.NewtonsoftJson;

namespace MarkSubsystem
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // ����������� ���������� ���� ������

            builder.Services.AddDbContext<UsersDbContext>(options =>
                options.UseNpgsql(builder.Configuration.GetConnectionString("UsersDb"))
                       .EnableSensitiveDataLogging()
                       .EnableDetailedErrors());

            // ����������� HttpClient
            builder.Services.AddHttpClient();

            // ����������� � Swagger
            builder.Services.AddControllers()
                .AddNewtonsoftJson();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            var app = builder.Build();

            

            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseAuthorization();
            app.MapControllers();

            app.Run();
        }
    }
}
