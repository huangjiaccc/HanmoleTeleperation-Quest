1. 在

   ```csharp
   GestureAndControllerInputModeManager
   ```

   里面已经有输入的逻辑检测
2. 当满足条件且摇杆按钮按下时，切换turntableInput状态：

   * 打开：设置DataManager.Instance.turntableInput = true

   * 关闭：设置DataManager.Instance.turntableInput = false，并将当前选中的索引赋值给DataManager.Instance.currobotState.task
3. 确保在UpdateTurntableSelection方法中正确获取turntableInput状态变化
4. 新增一个物体turntableobj，然后在turntableinput为true的状态下去检测是否有摇杆按钮按下去实现turntableobj的显示，最后关闭turntableobj的时候，把参数传给datamanager里面的currobotstate

