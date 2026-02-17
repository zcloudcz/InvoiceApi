using System.Linq.Expressions;
using InvoiceApi.Contracts.Common.Pagination;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.Application.Common.Extensions;

/// <summary>
/// Extension methods for IQueryable to support pagination, filtering and sorting
/// </summary>
public static class QueryableExtensions
{
    /// <summary>
    /// Converts IQueryable to PagedResult with pagination
    /// </summary>
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 100) pageSize = 100;

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<T>(items, totalCount, page, pageSize);
    }

    /// <summary>
    /// Applies sorting to IQueryable based on property name
    /// </summary>
    public static IQueryable<T> ApplySorting<T>(
        this IQueryable<T> query,
        string? sortBy,
        bool isDescending = false)
    {
        if (string.IsNullOrWhiteSpace(sortBy))
            return query;

        // Get property info
        var propertyInfo = typeof(T).GetProperty(sortBy,
            System.Reflection.BindingFlags.IgnoreCase |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance);

        if (propertyInfo == null)
            return query;

        // Create expression: x => x.PropertyName
        var parameter = Expression.Parameter(typeof(T), "x");
        var property = Expression.Property(parameter, propertyInfo);
        var lambda = Expression.Lambda(property, parameter);

        // Build OrderBy/OrderByDescending method name
        var methodName = isDescending ? "OrderByDescending" : "OrderBy";

        // Call OrderBy/OrderByDescending
        var resultExpression = Expression.Call(
            typeof(Queryable),
            methodName,
            new Type[] { typeof(T), propertyInfo.PropertyType },
            query.Expression,
            Expression.Quote(lambda));

        return query.Provider.CreateQuery<T>(resultExpression);
    }

    /// <summary>
    /// Applies filtering based on search string to multiple properties
    /// </summary>
    public static IQueryable<T> ApplySearch<T>(
        this IQueryable<T> query,
        string? searchString,
        params Expression<Func<T, string>>[] properties)
    {
        if (string.IsNullOrWhiteSpace(searchString) || properties.Length == 0)
            return query;

        var search = searchString.ToLower();
        var parameter = Expression.Parameter(typeof(T), "x");

        // Build OR expression for all properties
        Expression? orExpression = null;

        foreach (var property in properties)
        {
            // property.Body but with our parameter
            var visitor = new ParameterReplacer(property.Parameters[0], parameter);
            var propertyExpression = visitor.Visit(property.Body);

            // x.Property != null && x.Property.ToLower().Contains(search)
            var notNullExpression = Expression.NotEqual(
                propertyExpression,
                Expression.Constant(null, typeof(string)));

            var toLowerMethod = typeof(string).GetMethod("ToLower", Type.EmptyTypes)!;
            var toLowerExpression = Expression.Call(propertyExpression, toLowerMethod);

            var containsMethod = typeof(string).GetMethod("Contains", new[] { typeof(string) })!;
            var containsExpression = Expression.Call(
                toLowerExpression,
                containsMethod,
                Expression.Constant(search));

            var andExpression = Expression.AndAlso(notNullExpression, containsExpression);

            orExpression = orExpression == null
                ? andExpression
                : Expression.OrElse(orExpression, andExpression);
        }

        if (orExpression == null)
            return query;

        var lambda = Expression.Lambda<Func<T, bool>>(orExpression, parameter);
        return query.Where(lambda);
    }

    private class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression _oldParameter;
        private readonly ParameterExpression _newParameter;

        public ParameterReplacer(ParameterExpression oldParameter, ParameterExpression newParameter)
        {
            _oldParameter = oldParameter;
            _newParameter = newParameter;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            return node == _oldParameter ? _newParameter : base.VisitParameter(node);
        }
    }
}
