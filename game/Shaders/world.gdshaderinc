// Общее для всех шейдеров мира (docs/design/visual-style.md §7): глобальные uniform и тонирование инфо-режима.
// Значения пишет InfoMode.cs (RenderingServer.GlobalShaderParameterSet); объявлены в project.godot [shader_globals].
// Ни одного цвета здесь нет: беж инфо-режима и light_tint приходят из Palette.cs.
global uniform float info_mode;      // 0..1: переход основной вид -> инфо-режим (0.25 с)
global uniform int info_layer;       // 1 Трафик, 2 Пешеходы, 3 Зоны
global uniform vec3 light_tint;      // множитель для unshaded-материалов (VS10: ночью темнее); днём (1, 1, 1)
global uniform float cam_focus_dist; // расстояние камеры до точки фокуса, м (для затухания рёбер в шейдерах VS2+)
global uniform vec3 info_bg_dark;    // беж инфо-режима для тёмных исходных цветов (линейное пространство)
global uniform vec3 info_bg_light;   // и для светлых

// Всё приглушено в светлый беж по яркости исходного цвета; оверлей слоя накладывается ПОСЛЕ этого вызова (насыщенные цвета поверх беж).
// w - доля тонирования (0 - не тонировать: stuck/выделенные ТС остаются насыщенными).
vec3 world_info(vec3 c, float w) {
	float l = clamp(dot(c, vec3(0.2126, 0.7152, 0.0722)), 0.0, 1.0);
	return mix(c, mix(info_bg_dark, info_bg_light, l), info_mode * w);
}
