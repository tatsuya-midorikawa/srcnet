/* 计算面积 */
struct 坐标 { int 横; int 纵; };
int 面积(const struct 坐标 *点) { return 点->横 * 点->纵; }
