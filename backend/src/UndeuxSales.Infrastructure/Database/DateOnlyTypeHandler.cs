using System.Data;
using Dapper;

namespace UndeuxSales.Infrastructure.Database;

/// <summary>
/// Dapper に <see cref="DateOnly"/> の取り扱いを教える型ハンドラ。
/// パラメータ値は Npgsql がそのまま date 型へマップする。
/// </summary>
public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value;
    }

    public override DateOnly Parse(object value) => value switch
    {
        DateOnly dateOnly => dateOnly,
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        _ => throw new InvalidCastException(
            $"型 {value?.GetType().Name ?? "null"} を DateOnly に変換できません。"),
    };
}
