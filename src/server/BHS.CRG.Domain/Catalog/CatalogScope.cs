namespace BHS.CRG.Domain.Catalog;

/// <summary>
/// Уровень расположения объекта на единой оси (issue #84). Значение enum = приоритет при
/// разрешении иерархии: Set=1 (высший), Section=2, Construction=3, System=5 (низший).
/// </summary>
public enum CatalogScope { Set = 1, Section = 2, Construction = 3, System = 5 }
