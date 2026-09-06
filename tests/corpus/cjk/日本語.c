#include "共通.h"

/** 図形の面積を求める。 */
struct 座標 {
  int 横;
  int 縦;
};

// TODO: 単位の扱いを見直す
static int 面積(const struct 座標 *点) { return 点->横 * 点->縦; }

int 合計面積(const struct 座標 *点) { return 面積(点); }
