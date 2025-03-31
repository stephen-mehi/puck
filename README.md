# puck


# Networking and deployment
This repo uses github actions that target a windows agent. The deployment target is a NUC computer with ubuntu server running a microk8s cluster. Special networking configuration was required. The NUC has two network adapters: ethernet and wifi. Both needed to have static ip addresses. The wifi so the NUC can keep a consistent ip for reaching from browser and ssh, and the ethernet because it is hardwired to a network enabled modbus io device so has no way to have an ip address assigned from a dhcp server. The default network config tool on ubuntu server is networkd, but it seems to dynamically remove the static ip on ethernet adapters when ethernet cabels are unplugged. This is unacceptable as it forces pods to be restarted. Therefore networkd was disabled and NetworkManager was used in its place. NetPlan is the high level tool for specifying network configuration. This is the config used for the wifi stored at etc/netplan/<file>.yaml:

network:
  version: 2
  renderer: NetworkManager
  wifis:
    wlp0s20f3:
      access-points:
        NETGEAR09-5G:
          password: x
      dhcp4: no
      dhcp6: no
      addresses: [192.168.1.12/24]
      nameservers:
        addresses: [8.8.8.8, 8.8.8.4]
      routes:
        - to: default
          via: 192.168.1.1

Even with NetworkManager, the static ip for the ethernet port was removed when unplugged. Cloud-init was disabled and ifupdown was installed. The following lower level ifupdown config was created at etc/network/interfaces.d/<adapter_name>:

auto eno1
iface eno1 inet static
    address 192.168.2.100
    netmask 255.255.255.0
    gateway 192.168.2.1
    post-up ip route del default
    
This results in no dynamic changes to adapters. 
