using Microsoft.EntityFrameworkCore;
using EFCoreLearning.Data;

namespace EFCoreLearning.Services;

public sealed class LearningRepository(IDbContextFactory<LearningDbContext> contextFactory)
{
    public async Task<List<InventoryItem>> GetItemsAsync(bool activeOnly = false)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        var query = db.InventoryItems.AsNoTracking();
        if (activeOnly) query = query.Where(item => item.IsActive);
        return await query.OrderBy(item => item.Name).ToListAsync();
    }

    public async Task<InventoryItem> SaveItemAsync(InventoryItem input)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        InventoryItem entity;
        if (input.Id == 0)
        {
            entity = new InventoryItem();
            db.InventoryItems.Add(entity);
        }
        else
        {
            entity = await db.InventoryItems.FindAsync(input.Id)
                ?? throw new InvalidOperationException("The item no longer exists.");
        }
        entity.Sku = input.Sku.Trim(); entity.Name = input.Name.Trim(); entity.Price = input.Price;
        entity.Stock = input.Stock; entity.IsActive = input.IsActive;
        await db.SaveChangesAsync();
        return entity;
    }

    public async Task DeleteItemAsync(int id)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        await db.InventoryItems.Where(item => item.Id == id).ExecuteDeleteAsync();
    }

    public async Task<int> RestockAsync(int minimumStock, int amount)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.InventoryItems.Where(item => item.Stock < minimumStock)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Stock, item => item.Stock + amount));
    }

    public async Task<List<Customer>> GetCustomersWithOrdersAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Customers.AsNoTracking().Include(customer => customer.Orders)
            .AsSplitQuery().OrderBy(customer => customer.Name).ToListAsync();
    }

    public async Task<Order> AddOrderAsync(int customerId, string reference, decimal total, bool paid)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        if (!await db.Customers.AnyAsync(customer => customer.Id == customerId))
            throw new InvalidOperationException("Select an existing customer.");
        var order = new Order { CustomerId = customerId, Reference = reference.Trim(), Total = total, IsPaid = paid, CreatedAt = DateTime.UtcNow };
        db.Orders.Add(order); await db.SaveChangesAsync(); return order;
    }

    public async Task<List<Student>> GetStudentsWithCoursesAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Students.AsNoTracking().Include(student => student.Enrollments)
            .ThenInclude(enrollment => enrollment.Course).AsSplitQuery().OrderBy(student => student.Name).ToListAsync();
    }

    public async Task<List<Course>> GetCoursesAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Courses.AsNoTracking().OrderBy(course => course.Title).ToListAsync();
    }

    public async Task AddEnrollmentAsync(int studentId, int courseId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        if (await db.Enrollments.AnyAsync(item => item.StudentId == studentId && item.CourseId == courseId))
            throw new InvalidOperationException("The student is already enrolled in this course.");
        db.Enrollments.Add(new Enrollment { StudentId = studentId, CourseId = courseId, EnrolledAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    public async Task RemoveEnrollmentAsync(int studentId, int courseId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        await db.Enrollments.Where(item => item.StudentId == studentId && item.CourseId == courseId).ExecuteDeleteAsync();
    }

    public async Task<QueryExampleResult> RunExampleAsync(string key, decimal minimumTotal, string customerSearch)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        IQueryable<OrderRow> query;
        string title;
        string linq;

        switch (key)
        {
            case "filtered":
                title = "Filtered query";
                linq = ".Where(order => order.Total >= minimumTotal && order.IsPaid)";
                query = db.Orders.AsNoTracking().Where(order => order.Total >= minimumTotal && order.IsPaid)
                    .OrderByDescending(order => (double)order.Total)
                    .Select(order => new OrderRow(order.Reference, order.Customer!.Name, order.Total, order.IsPaid));
                break;
            case "join":
                title = "Explicit INNER JOIN with filter";
                linq = "from order in Orders join customer in Customers ... where customer.Name.Contains(search)";
                query = from order in db.Orders.AsNoTracking()
                        join customer in db.Customers on order.CustomerId equals customer.Id
                        where customer.Name.Contains(customerSearch)
                        orderby (double)order.Total descending
                        select new OrderRow(order.Reference, customer.Name, order.Total, order.IsPaid);
                break;
            case "raw":
                title = "Parameterized raw SQL";
                linq = "Orders.FromSql($\"SELECT * FROM Orders WHERE Total >= {minimumTotal}\")";
                query = db.Orders.FromSql($"SELECT * FROM Orders WHERE Total >= {minimumTotal}").AsNoTracking()
                    .OrderByDescending(order => (double)order.Total)
                    .Select(order => new OrderRow(order.Reference, order.Customer!.Name, order.Total, order.IsPaid));
                break;
            case "raw-all":
                title = "Unfiltered raw SQL";
                linq = "Orders.FromSqlRaw(\"SELECT * FROM Orders\")";
                query = db.Orders.FromSqlRaw("SELECT * FROM Orders").AsNoTracking()
                    .OrderByDescending(order => (double)order.Total)
                    .Select(order => new OrderRow(order.Reference, order.Customer!.Name, order.Total, order.IsPaid));
                break;
            default:
                title = "Unfiltered LINQ query";
                linq = "Orders.AsNoTracking().Select(...)";
                query = db.Orders.AsNoTracking()
                    .OrderByDescending(order => (double)order.Total)
                    .Select(order => new OrderRow(order.Reference, order.Customer!.Name, order.Total, order.IsPaid));
                break;
        }

        var sql = query.ToQueryString();
        var rows = await query.ToListAsync();
        return new QueryExampleResult(title, linq, sql, rows);
    }
}

public sealed record OrderRow(string Reference, string Customer, decimal Total, bool IsPaid);
public sealed record QueryExampleResult(string Title, string Linq, string Sql, IReadOnlyList<OrderRow> Rows);