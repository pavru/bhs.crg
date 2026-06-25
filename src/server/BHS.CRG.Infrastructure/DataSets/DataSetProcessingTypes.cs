namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Узел дерева фильтров.
/// type="condition" — условие (column + op + value?)
/// type="group"     — логическая группа (logic + children[])
/// </summary>
public class FilterNode
{
    public string Type { get; set; } = "group";

    // Condition fields
    public string? Column { get; set; }
    public string? Op { get; set; }
    public string? Value { get; set; }

    // Group fields
    public string Logic { get; set; } = "and";
    public FilterNode[]? Children { get; set; }
}

public class ComputedColumnDef
{
    public string Alias { get; set; } = "";
    public string Expr { get; set; } = "";
}
