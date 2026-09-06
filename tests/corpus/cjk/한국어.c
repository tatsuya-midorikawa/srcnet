/* 넓이 계산 */
struct 좌표 { int 가로; int 세로; };
int 넓이(const struct 좌표 *점) { return 점->가로 * 점->세로; }
