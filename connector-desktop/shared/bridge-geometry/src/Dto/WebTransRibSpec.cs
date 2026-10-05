namespace Platform.Bridge.Geometry.Dto;

/// <summary>
/// Поперечное ребро стенки (диафрагма-стиффенер). Перпендикулярно оси
/// балки. Несколько ребёр идут с заданным шагом Step.
/// </summary>
/// <param name="Side">"left" или "right" — на какой стороне стенки.</param>
/// <param name="BothSides">Зеркалить на противоположной стороне.</param>
/// <param name="Step">Шаг между соседними поперечными рёбрами (мм).</param>
/// <param name="Height">Вертикальная высота ребра (между поясами).</param>
/// <param name="Thickness">Толщина ребра.</param>
public readonly record struct WebTransRibSpec(
    string Side,
    bool BothSides,
    double Step,
    double Height,
    double Thickness);
