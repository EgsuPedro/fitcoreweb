namespace FitCore.Analytics.Domain;

/// <summary>Los reportes que se pueden exportar a CSV.</summary>
public enum ExportReport
{
    Collections = 1,
    PaymentMethods = 2,
    OutstandingBalances = 3,
    CashRegisters = 4,
    Reversals = 5,
    MemberAcquisitions = 6,
    MemberRetention = 7,
    MemberRenewals = 8,
    MemberExpirations = 9,
    Attendance = 10,
    ClassOccupancy = 11,
    ClassCancellations = 12,
    BranchComparison = 13
}
