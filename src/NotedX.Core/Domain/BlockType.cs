namespace NotedX.Core.Domain;

/// <summary>Kinds of blocks supported by the editor.</summary>
public enum BlockType
{
    Paragraph = 0,
    Heading1 = 1,
    Heading2 = 2,
    Heading3 = 3,
    BulletedList = 4,
    NumberedList = 5,
    ToDo = 6,
    Quote = 7,
    Code = 8,
    Divider = 9,
    Image = 10,
    File = 11,
    Table = 12
}
