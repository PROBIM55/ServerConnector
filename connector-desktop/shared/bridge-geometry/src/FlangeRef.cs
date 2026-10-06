namespace Platform.Bridge.Geometry;

/// <summary>
/// К какой плите пояса (верхней или нижней) привязана отметка при
/// размещении: TopLocked — верх плиты на отметке, BottomLocked — низ.
/// Применяется к каждому из поясов независимо (bottomRef, topRef).
/// </summary>
public enum FlangeRef
{
    TopLocked,
    BottomLocked,
}

/// <summary>
/// Режим интерпретации высоты h: TO_TOP — h это расстояние от системной
/// оси (низ нижнего пояса по умолчанию) до верха верхнего пояса;
/// TO_BOTTOM — h не учитывает толщину нижнего пояса (т.е. h = чистая
/// высота стенки + верхний пояс). Влияет на num16/num17 в Step4.
/// </summary>
public enum HeightMode
{
    ToTop,
    ToBottom,
}

/// <summary>
/// Режим верхней зоны балки.
/// TOP_FLANGE — верхний пояс (несколько сегментов L,W,T).
/// DECK — единая плита-палуба (deckW, deckT).
/// DECK_SLOPES — палуба заданной толщины со сложным уклонным профилем
/// слева и справа (несколько SlopeSeg с обеих сторон).
/// </summary>
public enum TopMode
{
    TopFlange,
    Deck,
    DeckSlopes,
}
