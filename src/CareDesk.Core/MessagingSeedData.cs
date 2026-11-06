namespace CareDesk.Core;

public static class MessagingSeedData
{
    public static StaffMember CurrentStaff { get; } = new(new(Guid.Parse("83e7c68c-626c-498d-ac8f-b72193c29e8d")), "Rachel Kang");
    public static StaffMember SecondStaff { get; } = new(new(Guid.Parse("4535d73f-c8da-4c44-82ae-3e9f53498b50")), "Barbara Boyajian");
    public static IReadOnlyList<StaffMember> Staff { get; } = [CurrentStaff, SecondStaff];

    public static ConversationId MadsConversationId { get; } = new(Guid.Parse("c48ff7d5-0d37-4e79-b7db-887e0708794b"));
    public static RequestId MadsRequestId { get; } = new(Guid.Parse("41c7563c-feda-48d0-8389-92cad01a689d"));
    public static ConversationId AnsonConversationId { get; } = new(Guid.Parse("3ef23f13-b9bf-4c24-98d7-c54c4f326e20"));
    public static RequestId AnsonRequestId { get; } = new(Guid.Parse("fa03e7e2-af16-4e49-88ae-b44dc6eabedc"));
    public static ConversationId JasonConversationId { get; } = new(Guid.Parse("36785948-1e1e-4924-879b-e02f28231adf"));
    public static RequestId JasonRequestId { get; } = new(Guid.Parse("0ed7cb1d-52d9-4ad0-a99a-c498ef707dc4"));
}
