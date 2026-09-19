namespace Domain.DTOs.Metrics.Enums;

// Outcome is an extraction's: under it only extraction events are grouped, so the breakdown is
// the share of turns that were gated, came back empty, extracted or failed.
public enum MemoryDimension { User, EventType, Agent, Outcome }