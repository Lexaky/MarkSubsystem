using Microsoft.EntityFrameworkCore;
using MarkSubsystem.Models;
using System.Collections.Generic;

namespace MarkSubsystem.Data;

public enum UserRole { Student, Teacher, Administrator }
public enum SessionType { Exam, Practice }

public class UsersDbContext : DbContext
{
    public UsersDbContext(DbContextOptions<UsersDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; }
    public DbSet<Session> Sessions { get; set; }
    public DbSet<Grade> Grades { get; set; }
    public DbSet<Group> Groups { get; set; }
    public DbSet<GroupSession> GroupSessions { get; set; }
    public DbSet<SessionTest> SessionTests { get; set; }
    public DbSet<SolutionsByUser> SolutionsByUsers { get; set; }
    public DbSet<VariablesSolutionsByUsers> VariablesSolutionsByUsers { get; set; }
    public DbSet<SolutionsByProgram> SolutionsByPrograms { get; set; }
    public DbSet<VariablesSolutionsByProgram> VariablesSolutionsByPrograms { get; set; }
    public DbSet<UserTestAbility> UserTestAbilities { get; set; }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // === Groups ===
        modelBuilder.Entity<Group>()
            .ToTable("Groups")
            .HasKey(g => g.GroupId);

        modelBuilder.Entity<Group>()
            .Property(g => g.GroupId)
            .HasColumnName("group_id");

        modelBuilder.Entity<Group>()
            .Property(g => g.Name)
            .HasColumnName("name");

        // === Users ===
        modelBuilder.Entity<User>()
            .ToTable("Users")
            .HasKey(u => u.UserId);

        modelBuilder.Entity<User>()
            .Property(u => u.UserId)
            .HasColumnName("user_id");

        modelBuilder.Entity<User>()
            .Property(u => u.GroupId)
            .HasColumnName("group_id");

        modelBuilder.Entity<User>()
            .Property(u => u.Role)
            .HasColumnName("role")
            .HasConversion<string>();

        modelBuilder.Entity<User>()
            .Property(u => u.Name)
            .HasColumnName("name");

        modelBuilder.Entity<User>()
            .Property(u => u.Login)
            .HasColumnName("login");

        modelBuilder.Entity<User>()
            .Property(u => u.Password)
            .HasColumnName("password");

        modelBuilder.Entity<User>()
            .Property(u => u.IconPath)
            .HasColumnName("icon_path");

        modelBuilder.Entity<User>()
            .HasOne<Group>()
            .WithMany()
            .HasForeignKey(u => u.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        // === Sessions ===
        modelBuilder.Entity<Session>()
            .ToTable("Sessions")
            .HasKey(s => s.SessionId);

        modelBuilder.Entity<Session>()
            .Property(s => s.SessionId)
            .HasColumnName("session_id");

        modelBuilder.Entity<Session>()
            .Property(s => s.Difficult)
            .HasColumnName("difficult");

        modelBuilder.Entity<Session>()
            .Property(s => s.DateStart)
            .HasColumnName("date_start");

        modelBuilder.Entity<Session>()
            .Property(s => s.DateFinish)
            .HasColumnName("date_finish");

        modelBuilder.Entity<Session>()
            .Property(s => s.Time)
            .HasColumnName("time");

        modelBuilder.Entity<Session>()
            .Property(s => s.SessionType)
            .HasColumnName("session_type")
            .HasConversion<string>();

        modelBuilder.Entity<Session>()
            .HasMany(s => s.SessionTests)
            .WithOne()
            .HasForeignKey(st => st.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // === Grades ===
        modelBuilder.Entity<Grade>()
            .ToTable("Grades")
            .HasKey(g => new { g.UserId, g.SessionId });

        modelBuilder.Entity<Grade>()
            .Property(g => g.UserId)
            .HasColumnName("user_id");

        modelBuilder.Entity<Grade>()
            .Property(g => g.SessionId)
            .HasColumnName("session_id");

        modelBuilder.Entity<Grade>()
            .Property(g => g.Mark)
            .HasColumnName("mark");

        modelBuilder.Entity<Grade>()
            .Property(g => g.Datetime)
            .HasColumnName("datetime");

        modelBuilder.Entity<Grade>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(g => g.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Grade>()
            .HasOne<Session>()
            .WithMany()
            .HasForeignKey(g => g.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // === GroupSessions ===
        modelBuilder.Entity<GroupSession>()
            .ToTable("GroupSessions")
            .HasKey(gs => new { gs.SessionId, gs.GroupId });

        modelBuilder.Entity<GroupSession>()
            .Property(gs => gs.SessionId)
            .HasColumnName("session_id");

        modelBuilder.Entity<GroupSession>()
            .Property(gs => gs.GroupId)
            .HasColumnName("group_id");

        modelBuilder.Entity<GroupSession>()
            .HasOne<Session>()
            .WithMany()
            .HasForeignKey(gs => gs.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GroupSession>()
            .HasOne<Group>()
            .WithMany()
            .HasForeignKey(gs => gs.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        // === SessionTests ===
        modelBuilder.Entity<SessionTest>()
            .ToTable("SessionTests")
            .HasKey(st => new { st.SessionId, st.TestId });

        modelBuilder.Entity<SessionTest>()
            .Property(st => st.SessionId)
            .HasColumnName("session_id");

        modelBuilder.Entity<SessionTest>()
            .Property(st => st.TestId)
            .HasColumnName("test_id");

        // Настройка SolutionsByUser
        modelBuilder.Entity<SolutionsByUser>()
            .HasKey(s => new { s.SessionId, s.UserId, s.UserStep, s.UserLineNumber, s.OrderNumber, s.TestId });

        modelBuilder.Entity<SolutionsByUser>()
            .HasOne(s => s.Session)
            .WithMany()
            .HasForeignKey(s => s.SessionId);

        modelBuilder.Entity<SolutionsByUser>()
            .HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId);

        // Настройка VariablesSolutionsByUsers
        modelBuilder.Entity<VariablesSolutionsByUsers>()
            .HasKey(v => new { v.UserStep, v.UserLineNumber, v.OrderNumber, v.TestId, v.VarName });

        // Настройка SolutionsByProgram
        modelBuilder.Entity<SolutionsByProgram>()
            .HasKey(s => new { s.SessionId, s.TestId, s.ProgramStep, s.ProgramLineNumber, s.OrderNumber });

        modelBuilder.Entity<SolutionsByProgram>()
            .HasOne(s => s.Session)
            .WithMany()
            .HasForeignKey(s => s.SessionId);

        // Настройка VariablesSolutionsByProgram
        modelBuilder.Entity<VariablesSolutionsByProgram>()
            .HasKey(v => new { v.ProgramStep, v.ProgramLineNumber, v.OrderNumber, v.TestId, v.VarName });

        // Настройка таблицы со способностями обучающихся
        modelBuilder.Entity<UserTestAbility>()
        .HasKey(uta => new { uta.UserId, uta.TestId });
    }
}

public class User
{
    public int UserId { get; set; }
    public int GroupId { get; set; }
    public UserRole Role { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Login { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
}

public class Session
{
    public int SessionId { get; set; }
    public float Difficult { get; set; } = 0.5f;
    public DateTime DateStart { get; set; }
    public DateTime DateFinish { get; set; }
    public TimeSpan Time { get; set; }
    public SessionType? SessionType { get; set; }
    public ICollection<SessionTest> SessionTests { get; set; } = new List<SessionTest>(); // Навигационное свойство
}

public class Grade
{
    public int UserId { get; set; }
    public int SessionId { get; set; }
    public float Mark { get; set; }
    public DateTime Datetime { get; set; }
}

public class Group
{
    public int GroupId { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class GroupSession
{
    public int SessionId { get; set; }
    public int GroupId { get; set; }
}

public class SessionTest
{
    public int SessionId { get; set; }
    public int TestId { get; set; }
}