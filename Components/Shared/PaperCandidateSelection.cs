namespace ExamOps.Components.Shared;

using ExamOps.Model.Dtos;

public sealed record PaperCandidateSelection(long SectionId, PaperCandidateRow Candidate);
