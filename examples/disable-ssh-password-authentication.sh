set -e

sudo sed -E -i 's|^#?\s*(PasswordAuthentication)\s.*|\1 no|' /etc/ssh/sshd_config
if ! grep '^PasswordAuthentication\s' /etc/ssh/sshd_config; then echo 'PasswordAuthentication no' |sudo tee -a /etc/ssh/sshd_config; fi


