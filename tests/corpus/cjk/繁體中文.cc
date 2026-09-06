namespace 圖形 {
class 方塊 {
 public:
  int 面積() const;
  int 邊長_;
};
int 方塊::面積() const { return 邊長_ * 邊長_; }
}  // namespace 圖形
