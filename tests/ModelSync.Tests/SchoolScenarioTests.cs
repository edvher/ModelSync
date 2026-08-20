using ModelSync.Core;
using Xunit;

namespace ModelSync.Tests;

/// <summary>
/// A realistic end-to-end use-case on the core service: a school's teacher and
/// student management. The registrar and the teachers' office each work in
/// their own private workspace of the star topology and synchronize through
/// the public branch.
///
/// The scenario deliberately exercises every property kind:
///  - single values  — names, titles, capacities;
///  - unordered sets — the subjects a teacher can teach;
///  - maps           — weekly office hours (day → time), grades (student → grade);
///  - ordered lists  — the course waiting list, where order is meaningful
///                     (first come, first served) and lists stay ≤ 3 items.
///
/// The metamodel (School/Teacher/Student/Course types) is built from ordinary
/// elements and synchronizes through the same operations as the model.
/// </summary>
public class SchoolScenarioTests
{
    private const string Registrar = "registrar";
    private const string TeacherOffice = "teacher-office";

    // Metamodel element ids: types are elements themselves (streamlined MOF).
    private const string SchoolType = "type-school";
    private const string TeacherType = "type-teacher";
    private const string StudentType = "type-student";
    private const string CourseType = "type-course";

    private static void AssertAllConverged(ModelService service)
    {
        var publicModel = service.GetModel(ModelService.PublicWorkspaceId);
        ModelAssert.Equivalent(publicModel, service.GetModel(Registrar));
        ModelAssert.Equivalent(publicModel, service.GetModel(TeacherOffice));
        var fresh = service.Checkout("fresh-" + Guid.NewGuid().ToString("N"));
        ModelAssert.Equivalent(publicModel, fresh);
    }

    /// <summary>
    /// The registrar publishes the metamodel; the teachers' office instantiates
    /// it. Both then edit concurrently — including a real conflict on the course
    /// title and concurrent waiting-list changes — and everything converges.
    /// </summary>
    [Fact]
    public void SchoolManagement_MetamodelAndModel_FullCollaboration()
    {
        var service = new ModelService();
        service.Checkout(Registrar);

        // ------------------------------------------------ 1) the metamodel
        service.Apply(Registrar, Op.Create(SchoolType));
        service.Apply(Registrar, Op.Set(SchoolType, "name", "School"));
        service.Apply(Registrar, Op.Put(SchoolType, "propertySchema", "name", "Single<String>"));
        service.Apply(Registrar, Op.Put(SchoolType, "propertySchema", "courses", "Set<Course>"));

        service.Apply(Registrar, Op.Create(TeacherType));
        service.Apply(Registrar, Op.Set(TeacherType, "name", "Teacher"));
        service.Apply(Registrar, Op.Put(TeacherType, "propertySchema", "name", "Single<String>"));
        service.Apply(Registrar, Op.Put(TeacherType, "propertySchema", "subjects", "Set<String>"));
        service.Apply(Registrar, Op.Put(TeacherType, "propertySchema", "officeHours", "Map<Day,Time>"));

        service.Apply(Registrar, Op.Create(StudentType));
        service.Apply(Registrar, Op.Set(StudentType, "name", "Student"));

        service.Apply(Registrar, Op.Create(CourseType));
        service.Apply(Registrar, Op.Set(CourseType, "name", "Course"));
        service.Apply(Registrar, Op.Put(CourseType, "propertySchema", "title", "Single<String>"));
        service.Apply(Registrar, Op.Put(CourseType, "propertySchema", "grades", "Map<Student,Grade>"));
        service.Apply(Registrar, Op.Put(CourseType, "propertySchema", "waitingList", "List<Student>"));

        // ------------------------------------- 2) students and the school
        service.Apply(Registrar, Op.Create("school-1", SchoolType));
        service.Apply(Registrar, Op.Set("school-1", "name", "Ada Lovelace High"));
        service.Apply(Registrar, Op.Create("s-ada", StudentType));
        service.Apply(Registrar, Op.Set("s-ada", "name", "Ada"));
        service.Apply(Registrar, Op.Create("s-grace", StudentType));
        service.Apply(Registrar, Op.Set("s-grace", "name", "Grace"));
        service.Apply(Registrar, Op.Create("s-linus", StudentType));
        service.Apply(Registrar, Op.Set("s-linus", "name", "Linus"));

        Assert.True(service.Commit(Registrar).Success);

        // -------------------------- 3) the teachers' office pulls everything
        service.Checkout(TeacherOffice);
        service.Update(TeacherOffice);

        var office = service.GetModel(TeacherOffice);
        Assert.Equal("Teacher", office.GetElement(TeacherType)!.GetProperty("name")!.SingleValue!.Content);
        Assert.Equal(StudentType, office.GetElement("s-ada")!.TypeId);
        Assert.Equal("List<Student>",
            office.GetElement(CourseType)!.GetProperty("propertySchema")!.MapValues["waitingList"].Content);

        // ------------------- 4) the office staffs a teacher and a course
        service.Apply(TeacherOffice, Op.Create("t-turing", TeacherType));
        service.Apply(TeacherOffice, Op.Set("t-turing", "name", "Alan Turing"));
        service.Apply(TeacherOffice, Op.AddSet("t-turing", "subjects", "math"));
        service.Apply(TeacherOffice, Op.AddSet("t-turing", "subjects", "computer-science"));
        service.Apply(TeacherOffice, Op.Put("t-turing", "officeHours", "monday", "10:00"));
        service.Apply(TeacherOffice, Op.Put("t-turing", "officeHours", "friday", "14:00"));

        service.Apply(TeacherOffice, Op.Create("c-algo", CourseType));
        service.Apply(TeacherOffice, Op.Set("c-algo", "title", "Algorithms"));
        service.Apply(TeacherOffice, Op.Set("c-algo", "teacher", "t-turing"));

        Assert.True(service.Commit(TeacherOffice).Success);
        service.Update(Registrar);

        // --------------------------- 5) concurrent, partially conflicting work
        // Registrar: enrolls the waiting list in arrival order (Ada, Grace) and
        // renames the course.
        service.Apply(Registrar, Op.Insert("c-algo", "waitingList", "w-ada", "s-ada", null));
        service.Apply(Registrar, Op.Insert("c-algo", "waitingList", "w-grace", "s-grace", "w-ada"));
        service.Apply(Registrar, Op.Set("c-algo", "title", "Algorithms and Data Structures"));
        service.Apply(Registrar, Op.Put("c-algo", "grades", "s-ada", "A"));

        // Office: renames the same course differently (real conflict), grades a
        // different student (no conflict), extends the office hours, and drops
        // the friday slot the registrar never touched.
        service.Apply(TeacherOffice, Op.Set("c-algo", "title", "Advanced Algorithms"));
        service.Apply(TeacherOffice, Op.Put("c-algo", "grades", "s-grace", "B"));
        service.Apply(TeacherOffice, Op.Put("t-turing", "officeHours", "wednesday", "09:00"));
        service.Apply(TeacherOffice, Op.RemoveMap("t-turing", "officeHours", "friday"));
        service.Apply(TeacherOffice, Op.RemoveSet("t-turing", "subjects", "math"));

        // Registrar commits first; the office updates, resolves and commits.
        Assert.True(service.Commit(Registrar).Success);
        var update = service.Update(TeacherOffice, ResolutionStrategy.ChildWins);
        Assert.Contains(update.Conflicts, c =>
            c.Category == ConflictCategory.SingleValue && c.Severity == ConflictSeverity.Real);
        Assert.True(service.Commit(TeacherOffice).Success);
        service.Update(Registrar);

        AssertAllConverged(service);

        var model = service.GetModel(ModelService.PublicWorkspaceId);
        var course = model.GetElement("c-algo")!;

        // ChildWins: the updating office's title survives the real conflict.
        Assert.Equal("Advanced Algorithms", course.GetProperty("title")!.SingleValue!.Content);

        // Both grades merged — different map keys never conflict.
        Assert.Equal("A", course.GetProperty("grades")!.MapValues["s-ada"].Content);
        Assert.Equal("B", course.GetProperty("grades")!.MapValues["s-grace"].Content);

        // The waiting list keeps its arrival order.
        Assert.Equal(new[] { "s-ada", "s-grace" },
            course.GetProperty("waitingList")!.ListItems.Select(i => i.Value.Content));

        var teacher = model.GetElement("t-turing")!;
        Assert.Equal(new[] { "computer-science" },
            teacher.GetProperty("subjects")!.SetValues.Select(v => v.Content));
        Assert.Equal(new[] { "monday=10:00", "wednesday=09:00" },
            teacher.GetProperty("officeHours")!.MapValues
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}={p.Value.Content}"));
    }

    /// <summary>
    /// Concurrent waiting-list management: both sides insert and remove on the
    /// same bounded list (max 3 students). Delete wins for removed entries,
    /// no insert is lost, and the arrival order of surviving entries is kept.
    /// </summary>
    [Fact]
    public void WaitingList_ConcurrentEnrollmentAndDrop_Converges()
    {
        var service = new ModelService();
        service.Checkout(Registrar);
        service.Apply(Registrar, Op.Create(CourseType));
        service.Apply(Registrar, Op.Create("c-algo", CourseType));
        service.Apply(Registrar, Op.Insert("c-algo", "waitingList", "w-ada", "s-ada", null));
        service.Apply(Registrar, Op.Insert("c-algo", "waitingList", "w-grace", "s-grace", "w-ada"));
        Assert.True(service.Commit(Registrar).Success);

        service.Checkout(TeacherOffice);
        service.Update(TeacherOffice);

        // Registrar: Ada gets a seat and leaves the waiting list; Linus arrives
        // at its end. The office concurrently queues Linus behind Ada too — the
        // same student, tracked under the office's own entry id.
        service.Apply(Registrar, Op.RemoveItem("c-algo", "waitingList", "w-ada"));
        service.Apply(Registrar, Op.Insert("c-algo", "waitingList", "w-linus", "s-linus", "w-grace"));
        service.Apply(TeacherOffice, Op.Insert("c-algo", "waitingList", "w-office-linus", "s-linus", "w-ada"));

        Assert.True(service.Commit(Registrar).Success);
        var update = service.Update(TeacherOffice, ResolutionStrategy.ChildWins);
        Assert.Contains(update.Conflicts, c => c.Category == ConflictCategory.ListAnchorDeleted);
        Assert.True(service.Commit(TeacherOffice).Success);
        service.Update(Registrar);

        AssertAllConverged(service);

        var values = ModelAssert.ListValues(service.GetModel(ModelService.PublicWorkspaceId), "c-algo", "waitingList");

        // Ada's entry is gone (delete wins); no insert was lost; the list
        // honors its bound of three entries.
        Assert.DoesNotContain("s-ada", values);
        Assert.Equal(2, values.Count(v => v == "s-linus"));
        Assert.Contains("s-grace", values);
        Assert.True(values.Count <= 3);

        // The office's entry was re-anchored to Ada's closest surviving
        // predecessor — the head — so it precedes Grace; the registrar's own
        // Linus entry stays behind Grace.
        Assert.Equal(new[] { "s-linus", "s-grace", "s-linus" }, values);
    }

    /// <summary>
    /// A student graduates (element delete) while the office concurrently
    /// grades them (constructive edit): a real DMC existence conflict. Under
    /// ParentWins the deletion stands everywhere; the grade edit is preserved
    /// in history but the element stays deleted.
    /// </summary>
    [Fact]
    public void Graduation_DeleteVersusConcurrentGrade_DeletionStandsUnderParentWins()
    {
        var service = new ModelService();
        service.Checkout(Registrar);
        service.Apply(Registrar, Op.Create(StudentType));
        service.Apply(Registrar, Op.Create("s-ada", StudentType));
        service.Apply(Registrar, Op.Set("s-ada", "name", "Ada"));
        Assert.True(service.Commit(Registrar).Success);

        service.Checkout(TeacherOffice);
        service.Update(TeacherOffice);

        service.Apply(Registrar, Op.Delete("s-ada"));
        service.Apply(TeacherOffice, Op.Set("s-ada", "finalGrade", "A+"));

        Assert.True(service.Commit(Registrar).Success);
        var update = service.Update(TeacherOffice, ResolutionStrategy.ParentWins);
        Assert.Contains(update.Conflicts, c =>
            c.Category == ConflictCategory.ElementExistence &&
            c.MergeType == MergeConflictType.Dmc &&
            c.Severity == ConflictSeverity.Real);
        Assert.True(service.Commit(TeacherOffice).Success);
        service.Update(Registrar);

        AssertAllConverged(service);
        Assert.Null(service.GetModel(ModelService.PublicWorkspaceId).GetElement("s-ada"));
    }
}
