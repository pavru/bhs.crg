using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

/// <summary>
/// Таблица перечня работ (ТЗ CORE-10, issue #964). Колонок ровно пять: свой идентификатор и четыре
/// составляющие ключа. Ни объёма, ни цены, ни сроков, ни статуса — и места под них тоже нет.
/// </summary>
public class WorkPlanItemConfiguration : IEntityTypeConfiguration<WorkPlanItem>
{
    public void Configure(EntityTypeBuilder<WorkPlanItem> b)
    {
        b.ToTable("work_plan_items");
        b.HasKey(e => e.Id);

        // Ключ уникален — целиком, вместе с пустым разделом.
        //
        // ⚠️ NULLS NOT DISTINCT обязательно. По умолчанию PostgreSQL считает NULL в уникальном
        // индексе РАЗНЫМИ значениями, то есть две позиции «прокладка кабеля, стройка, без раздела,
        // м» легли бы обе — а «стройка в целом» это штатный случай (ТЗ CORE-Q2), не редкость.
        // Дальше «найди или создай» находил бы то одну, то другую, и модули развесили бы свои
        // мнения на разных позициях с одинаковым смыслом. Нужен PostgreSQL 15+; у нас 18.
        b.HasIndex(e => new { e.WorkTypeId, e.ConstructionId, e.SectionId, e.UnitId })
            .IsUnique()
            .AreNullsDistinct(false);

        // Стройка и раздел — КОЛОНКАМИ (решение по ТЗ CORE-Q3, 18.09.2026), а не полиморфной осью
        // расположения: позиция принадлежит перечню конкретной стройки, и полиморфизм здесь
        // означал бы позицию на уровне комплекта, которой в ТЗ нет.
        //
        // Каскад: стройки и раздела не стало — перечень адресовать нечем, позиции уходят с ними.
        // ⚠️ Каскад базы обходит правило «удаляем только то, на что не ссылаются» с фланга, поэтому
        // удаление уровня спрашивает держателей ссылок ПРИКЛАДНЫМ каскадом (IScopeCascade) — ровно
        // тем же приёмом, каким это уже сделано для объектов уровня (issue #739).
        b.HasOne<Construction>().WithMany()
            .HasForeignKey(e => e.ConstructionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Section>().WithMany()
            .HasForeignKey(e => e.SectionId).OnDelete(DeleteBehavior.Cascade);

        // Вид работы и единица — ЗАПРЕТ удаления цели: запись классификатора, на которую ссылается
        // перечень, унесла бы смысл позиции, а модули продолжали бы держать на неё свои мнения.
        // Человеческий отказ на этом пути даёт обработчик удаления записи общих данных; ключ базы —
        // последний рубеж, чтобы висячей ссылки не появилось и в обход обработчика.
        b.HasOne<DomainObject>().WithMany()
            .HasForeignKey(e => e.WorkTypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DomainObject>().WithMany()
            .HasForeignKey(e => e.UnitId).OnDelete(DeleteBehavior.Restrict);

        // Перечень читают «по стройке» — это единственная выборка, которая будет у него всегда.
        b.HasIndex(e => new { e.ConstructionId, e.SectionId });
    }
}
