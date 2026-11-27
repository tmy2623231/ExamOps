namespace ExamOps.Components.Shared;

public sealed class QuestionOptionEdit
{
    public string Label { get; set; } = "A";
    public string Content { get; set; } = "";
    public bool IsCorrect { get; set; }
    public int SortOrder { get; set; } = 1;
}
