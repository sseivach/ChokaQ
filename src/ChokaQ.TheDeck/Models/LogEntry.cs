namespace ChokaQ.TheDeck.Models;

public record LogEntry(DateTime Timestamp, string Message, string Level = "Info");
